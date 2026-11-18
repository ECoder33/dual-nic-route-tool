namespace DualNicRouteTool.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>阶段标题，如“开始配置双网卡路由”。</summary>
    Title,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string ToLine()
    {
        var tag = Level switch
        {
            LogLevel.Success => "成功",
            LogLevel.Warning => "警告",
            LogLevel.Error => "失败",
            LogLevel.Title => "阶段",
            _ => "信息",
        };
        return $"[{TimeText}] [{tag}] {Message}";
    }
}
