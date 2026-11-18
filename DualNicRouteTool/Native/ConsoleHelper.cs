using System.IO;
using System.Runtime.InteropServices;

namespace DualNicRouteTool.Native;

/// <summary>
/// 本程序是窗口程序（WinExe），默认没有控制台。--debug 需要把文本输出到命令行，
/// 这里负责判断标准输出是否可用，并在需要时附着到启动它的那个控制台。
/// </summary>
internal static class ConsoleHelper
{
    private const int StdOutputHandle = -11;
    private const uint FileTypeUnknown = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    /// <summary>
    /// 把文本写到标准输出。成功返回 true；既没有被重定向、也没有可附着的控制台时返回 false
    /// （例如从资源管理器双击启动）。
    /// </summary>
    public static bool TryWrite(string text)
    {
        // 先附着到父进程的控制台（从 cmd / PowerShell 里运行 exe）。即使标准输出已被重定向到
        // 管道 / 文件也要附着：只有这样才能得知控制台的代码页并按它编码，管道另一端才能正确
        // 解码中文；否则 .NET 会退回系统 ANSI 代码页，在 UTF-8 控制台里就是乱码。
        // 附着不会替换已经有效的标准句柄。宿主本身是控制台程序（dotnet xxx.dll）时附着会失败，
        // 但句柄本来就可用。
        var attached = IpHelper.AttachConsole(IpHelper.AttachParentProcess);
        if (!attached && !HasStandardOutput()) return false;

        try
        {
            Console.Out.Write(text);
            Console.Out.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool HasStandardOutput()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        return GetFileType(handle) != FileTypeUnknown;
    }
}
