using System.Text.Json.Serialization;

namespace DualNicRouteTool.Models;

/// <summary>路由来源（对应 Windows 的 NL_ROUTE_ORIGIN）。</summary>
public enum RouteOrigin
{
    Manual = 0,
    WellKnown = 1,
    Dhcp = 2,
    RouterAdvertisement = 3,
    SixToFour = 4,
}

/// <summary>一条 IPv4 路由。</summary>
public sealed record RouteEntry
{
    /// <summary>目标网络地址，如 10.0.0.0。</summary>
    public string Destination { get; init; } = "0.0.0.0";

    public int PrefixLength { get; init; }

    /// <summary>下一跳网关；0.0.0.0 表示在链路上（On-link）。</summary>
    public string NextHop { get; init; } = "0.0.0.0";

    public uint InterfaceIndex { get; init; }

    /// <summary>接口 LUID。接口索引在重启后可能变化，LUID 更稳定，匹配时优先使用。</summary>
    public ulong InterfaceLuid { get; init; }

    /// <summary>路由自身的跃点数（不含接口跃点；route print 显示的是两者之和）。</summary>
    public uint Metric { get; init; }

    public RouteOrigin Origin { get; init; }

    /// <summary>仅用于显示。</summary>
    public string? InterfaceName { get; init; }

    [JsonIgnore] public bool IsDefault => PrefixLength == 0 && Destination == "0.0.0.0";

    [JsonIgnore] public bool IsOnLink => NextHop == "0.0.0.0";

    [JsonIgnore] public string Prefix => $"{Destination}/{PrefixLength}";

    [JsonIgnore] public string NextHopText => IsOnLink ? "在链路上" : NextHop;

    [JsonIgnore] public string InterfaceText => string.IsNullOrEmpty(InterfaceName) ? $"接口 {InterfaceIndex}" : InterfaceName;

    public bool IsOn(uint interfaceIndex, ulong interfaceLuid) =>
        InterfaceLuid != 0 && interfaceLuid != 0 ? InterfaceLuid == interfaceLuid : InterfaceIndex == interfaceIndex;

    public bool IsOn(RouteEndpoint endpoint) => IsOn(endpoint.InterfaceIndex, endpoint.InterfaceLuid);

    public bool HasPrefix(Ipv4Network network) =>
        PrefixLength == network.PrefixLength && Destination == network.AddressText;

    /// <summary>是否为同一条路由：目标、前缀、下一跳、接口都相同（跃点数不参与比较）。</summary>
    public bool SameRoute(RouteEntry other) =>
        PrefixLength == other.PrefixLength
        && Destination == other.Destination
        && NextHop == other.NextHop
        && IsOn(other.InterfaceIndex, other.InterfaceLuid);

    public string Describe() => $"{Prefix} → {NextHopText}  [{InterfaceText}]  跃点 {Metric}";
}

/// <summary>路由的一端：某块网卡及其网关。</summary>
/// <param name="Gateway">网关地址；外网允许为空（表示沿用该网卡上已有的在链路默认路由，如拨号连接）。</param>
/// <param name="LocalAddress">网卡自身的 IPv4 地址，仅用于生成提示。</param>
public sealed record RouteEndpoint(
    uint InterfaceIndex,
    ulong InterfaceLuid,
    string InterfaceName,
    string? Gateway,
    string? LocalAddress = null,
    int LocalPrefixLength = 0);

/// <summary>一次配置请求。</summary>
public sealed record RouteRequest(RouteEndpoint Wan, RouteEndpoint Lan, IReadOnlyList<Ipv4Network> Networks);
