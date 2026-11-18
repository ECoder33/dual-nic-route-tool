namespace DualNicRouteTool.Models;

/// <summary>网卡类型（决定显示哪个图标）。</summary>
public enum NicKind
{
    Ethernet,
    WiFi,
    Cellular,
    Bluetooth,
    Virtual,
    Vpn,
    Dialup,
    Unknown,
}

/// <summary>一块已连接、拥有 IPv4 地址的网卡。</summary>
public sealed record NicInfo
{
    /// <summary>网卡 GUID，重启后保持不变，用于记住用户的选择。</summary>
    public required string Id { get; init; }

    /// <summary>连接名称，如“以太网”“WLAN”。</summary>
    public required string Name { get; init; }

    /// <summary>设备描述，如“Intel(R) Wi-Fi 6 AX201 160MHz”。</summary>
    public string Description { get; init; } = string.Empty;

    public NicKind Kind { get; init; }

    public uint Index { get; init; }

    public ulong Luid { get; init; }

    public required string Address { get; init; }

    public int PrefixLength { get; init; }

    /// <summary>系统当前上报的 IPv4 默认网关；没有则为 null。</summary>
    public string? Gateway { get; init; }

    public long SpeedBps { get; init; }

    public bool DhcpEnabled { get; init; }

    /// <summary>接口跃点数（route print 中显示的跃点 = 路由跃点 + 接口跃点）；读取失败为 null。</summary>
    public uint? InterfaceMetric { get; init; }

    public string KindLabel => Kind switch
    {
        NicKind.Ethernet => "以太网",
        NicKind.WiFi => "Wi-Fi",
        NicKind.Cellular => "移动宽带",
        NicKind.Bluetooth => "蓝牙",
        NicKind.Virtual => "虚拟网卡",
        NicKind.Vpn => "VPN / 隧道",
        NicKind.Dialup => "拨号连接",
        _ => "其他",
    };

    /// <summary>下拉项里跟在连接名称后面的类型说明；名称本身就是类型名（如“以太网”）时不再重复。</summary>
    public string KindSuffix =>
        Name.Trim().Equals(KindLabel, StringComparison.OrdinalIgnoreCase) ? string.Empty : $"  · {KindLabel}";

    public string SpeedLabel => SpeedBps switch
    {
        <= 0 => string.Empty,
        >= 1_000_000_000 => $"{SpeedBps / 1_000_000_000d:0.#} Gbps",
        >= 1_000_000 => $"{SpeedBps / 1_000_000d:0.#} Mbps",
        _ => $"{SpeedBps / 1_000d:0.#} Kbps",
    };

    /// <summary>类型与速率，如“以太网 · 1 Gbps”。</summary>
    public string KindAndSpeed => string.IsNullOrEmpty(SpeedLabel) ? KindLabel : $"{KindLabel} · {SpeedLabel}";

    /// <summary>地址信息行，如“IP 192.168.1.23/24 · DHCP · 接口 12”。</summary>
    public string InfoLine => $"IP {Address}/{PrefixLength} · {(DhcpEnabled ? "DHCP" : "静态")} · 接口 {Index}";

    public Ipv4Network? Subnet =>
        Ipv4.TryParse(Address, out var ip) ? new Ipv4Network(ip & Ipv4.Mask(PrefixLength), PrefixLength) : null;

    public RouteEndpoint ToEndpoint(string? gateway) =>
        new(Index, Luid, Name, string.IsNullOrWhiteSpace(gateway) ? null : gateway.Trim(), Address, PrefixLength);
}
