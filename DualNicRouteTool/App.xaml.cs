using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DualNicRouteTool.Native;
using DualNicRouteTool.Services;
using DualNicRouteTool.ViewModels;
using DualNicRouteTool.Views;

namespace DualNicRouteTool;

public partial class App : Application
{
    private const string MutexName = @"Global\DualNicRouteTool.SingleInstance";
    private const string ActivateEventName = @"Global\DualNicRouteTool.Activate";

    /// <summary>提权重启时传给新进程：等旧进程退出后再检查单实例。</summary>
    private const string WaitArgument = "--wait-for-exit";

    private static Mutex? _mutex;
    private static bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        var args = e.Args;

        if (HasFlag(args, "--help", "-h", "/?"))
        {
            ShowUsage();
            Shutdown(0);
            return;
        }

        if (IndexOf(args, "--debug") is var debugIndex and >= 0)
        {
            // 只读诊断：不占用单实例，不修改任何东西
            var file = debugIndex + 1 < args.Length && !args[debugIndex + 1].StartsWith('-') ? args[debugIndex + 1] : null;
            RunDiagnostics(file);
            return;
        }

        if (!AcquireSingleInstance(wait: HasFlag(args, WaitArgument)))
        {
            SignalRunningInstance();
            Shutdown(0);
            return;
        }

        ThemeService.Initialize(this);

        var viewModel = new MainViewModel(new NativeRouteTable(), new BackupStore(), new SettingsStore(), new DialogService());
        var window = new MainWindow(viewModel);
        MainWindow = window;
        ListenForActivation(window);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    // ------------------------------------------------------------------
    // 命令行：--debug / --help
    // ------------------------------------------------------------------

    private void RunDiagnostics(string? file)
    {
        string report;
        try
        {
            report = DiagnosticsReport.Build(new NativeRouteTable(), new BackupStore());
        }
        catch (Exception ex)
        {
            report = $"生成诊断信息失败：{ex}";
        }

        if (file is not null)
        {
            try
            {
                var path = Path.GetFullPath(file);
                File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                ConsoleHelper.TryWrite($"诊断信息已写入 {path}{Environment.NewLine}");
                Shutdown(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var message = $"无法写入 {file}：{ex.Message}";
                if (!ConsoleHelper.TryWrite(message + Environment.NewLine))
                {
                    ThemeService.Initialize(this);
                    new MessageDialog("诊断信息", message, DialogKind.Error).ShowDialog();
                }

                Shutdown(1);
            }

            return;
        }

        if (ConsoleHelper.TryWrite(report))
        {
            Shutdown(0);
            return;
        }

        // 没有可用的控制台（例如通过快捷方式启动）：改用窗口显示
        ThemeService.Initialize(this);
        var window = new DiagnosticsWindow(report);
        MainWindow = window;
        window.Show();
    }

    private void ShowUsage()
    {
        var exe = Path.GetFileName(Environment.ProcessPath) ?? "DualNicRouteTool.exe";
        var text =
            $"{AppEnvironment.AppName} v{AppEnvironment.Version}{Environment.NewLine}{Environment.NewLine}" +
            $"用法：{Environment.NewLine}" +
            $"  {exe}                  打开图形界面{Environment.NewLine}" +
            $"  {exe} --debug          输出只读诊断信息（网卡、路由表、备份），不修改任何设置{Environment.NewLine}" +
            $"  {exe} --debug <文件>   把诊断信息写入指定文件（UTF-8）{Environment.NewLine}" +
            $"  {exe} --help           显示本说明{Environment.NewLine}";

        if (ConsoleHelper.TryWrite(text)) return;

        ThemeService.Initialize(this);
        new MessageDialog("命令行参数", text, DialogKind.Info).ShowDialog();
    }

    private static bool HasFlag(string[] args, params string[] names) =>
        args.Any(a => names.Contains(a, StringComparer.OrdinalIgnoreCase));

    private static int IndexOf(string[] args, string name) =>
        Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // 单实例：两个实例同时改路由表会互相干扰
    // ------------------------------------------------------------------

    private static bool AcquireSingleInstance(bool wait)
    {
        try
        {
            _mutex = new Mutex(false, MutexName);
            _ownsMutex = _mutex.WaitOne(wait ? TimeSpan.FromSeconds(8) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出，互斥体已归当前线程所有
            _ownsMutex = true;
        }
        catch (UnauthorizedAccessException)
        {
            // 已有一个更高权限的实例在运行
            _ownsMutex = false;
        }

        return _ownsMutex;
    }

    private static void ReleaseSingleInstance()
    {
        if (_mutex is null) return;
        try
        {
            if (_ownsMutex) _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不在创建它的线程上，或已经释放
        }

        _ownsMutex = false;
        _mutex.Dispose();
        _mutex = null;
    }

    /// <summary>第一个实例监听激活事件：再次启动程序时把已有窗口带到前台。</summary>
    private void ListenForActivation(Window window)
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return;
        }

        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                // 系统可能不允许后台进程抢前台；短暂置顶可以保证窗口至少浮到最上面
                window.Topmost = true;
                window.Topmost = false;
            }), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle) handle.Set();
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 已运行的实例权限更高，无法通知它
        }

        new MessageDialog("程序已在运行", $"{AppEnvironment.AppName} 已经打开，请在任务栏中切换到它。", DialogKind.Info).ShowDialog();
    }

    // ------------------------------------------------------------------
    // 以管理员身份重启
    // ------------------------------------------------------------------

    public static void RestartAsAdministrator()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };

        // 通过 “dotnet DualNicRouteTool.dll” 启动时，宿主是 dotnet.exe，需要把 dll 路径传回去
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrEmpty(dll)) return;
            info.ArgumentList.Add(Path.GetFullPath(dll));
        }

        info.ArgumentList.Add(WaitArgument);

        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Win32Exception)
        {
            // 用户在 UAC 提示里点了“否”：留在当前的只读模式
            return;
        }

        Current.Shutdown(0);
    }

    // ------------------------------------------------------------------
    // 未处理的异常
    // ------------------------------------------------------------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var logPath = WriteCrashLog(e.Exception);

        // 主窗口还没显示出来就出错（启动失败）：提示之后必须退出，否则会留下一个没有窗口的进程
        var startupFailed = MainWindow is not { IsVisible: true };

        try
        {
            var owner = startupFailed ? null : MainWindow;
            new MessageDialog(
                "程序遇到了意外错误",
                $"{e.Exception.Message}\n\n" +
                "路由表的改动只在“一键配置 / 恢复原路由”执行期间发生；如果错误出现在那期间，请到“备份历史”里检查并恢复。" +
                (logPath is null ? string.Empty : $"\n\n错误详情已保存到：\n{logPath}"),
                DialogKind.Error) { Owner = owner }.ShowDialog();
        }
        catch (Exception)
        {
            // 连错误提示都显示不出来（例如资源加载失败）：退出，避免停在不可用的状态
            startupFailed = true;
        }

        if (startupFailed) Shutdown(1);
    }

    /// <summary>把异常写入崩溃日志，返回日志路径；写不进去返回 null。</summary>
    private static string? WriteCrashLog(Exception? exception)
    {
        if (exception is null) return null;

        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppEnvironment.Version}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";

        // 数据目录需要管理员权限才能写；只读模式下退到临时目录
        foreach (var directory in new[] { AppEnvironment.DataDirectory, Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "DualNicRouteTool_crash.log");
                File.AppendAllText(path, text, Encoding.UTF8);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 试下一个目录
            }
        }

        return null;
    }
}
