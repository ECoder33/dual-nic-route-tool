namespace DualNicRouteTool.Models;

/// <summary>保存到磁盘的用户设置。</summary>
public sealed class AppSettings
{
    public const string DefaultNetworks = "10.0.0.0/8;172.16.0.0/12;192.168.0.0/16";

    public string? WanInterfaceId { get; set; }

    public string? LanInterfaceId { get; set; }

    /// <summary>每块网卡上次使用的网关（键为网卡 GUID）。配置后内网默认路由被删除，系统不再上报其网关，靠这里记住。</summary>
    public Dictionary<string, string> Gateways { get; set; } = [];

    public List<string> Networks { get; set; } = [.. DefaultNetworks.Split(';')];

    /// <summary>配置完成后的守护轮次。</summary>
    public int GuardRounds { get; set; } = 5;

    /// <summary>每轮守护的间隔秒数。</summary>
    public double GuardIntervalSeconds { get; set; } = 1.5;

    /// <summary>程序运行期间持续巡检并清理重新出现的非外网默认路由。</summary>
    public bool PersistentGuard { get; set; }

    public int PersistentGuardIntervalSeconds { get; set; } = 5;
}
