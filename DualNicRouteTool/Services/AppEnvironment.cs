using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace DualNicRouteTool.Services;

/// <summary>程序运行环境信息。</summary>
public static class AppEnvironment
{
    public const string AppName = "双网卡路由配置工具";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.0.0";

    /// <summary>
    /// 数据目录。路由表是整机共享的，所以放在 ProgramData 而不是某个用户的个人目录：
    /// 即使用另一个管理员账号提权运行，看到的也是同一份备份。
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DualNicRouteTool");

    public static string BackupDirectory => Path.Combine(DataDirectory, "backups");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>系统启动时间（TickCount64 包含睡眠与休眠时间，所以结果在一次开机期间保持稳定）。</summary>
    public static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
