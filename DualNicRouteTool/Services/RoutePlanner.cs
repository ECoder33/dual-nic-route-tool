using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

/// <summary>
/// 根据“当前路由表 + 用户选择”计算要做的改动。纯函数，不访问系统，
/// 同一份结果用于界面预览、实际执行和单元测试。
/// </summary>
public static class RoutePlanner
{
    /// <summary>本工具新增路由使用的跃点数（与原 Python 版的 metric 1 一致）。</summary>
    public const uint RouteMetric = 1;

    /// <param name="current">当前 IPv4 路由表。</param>
    /// <param name="previouslyAdded">上一次配置（尚未恢复）时本工具新增的路由，用于清理这次不再需要的旧路由。</param>
    public static RoutePlan Build(
        RouteRequest request,
        IReadOnlyList<RouteEntry> current,
        IReadOnlyList<RouteEntry>? previouslyAdded = null)
    {
        previouslyAdded ??= [];
        var wan = request.Wan;
        var lan = request.Lan;
        var errors = new List<string>();
        var warnings = new List<string>();

        // ---------- 校验 ----------
        var sameInterface = wan.InterfaceLuid != 0 && lan.InterfaceLuid != 0
            ? wan.InterfaceLuid == lan.InterfaceLuid
            : wan.InterfaceIndex == lan.InterfaceIndex;
        if (sameInterface) errors.Add("外网网卡和内网网卡不能是同一块网卡。");

        uint wanGateway = 0;
        var wanGatewayGiven = !string.IsNullOrWhiteSpace(wan.Gateway);
        if (wanGatewayGiven && !Ipv4.TryParseGateway(wan.Gateway, out wanGateway))
            errors.Add($"外网网关“{wan.Gateway}”不是有效的 IPv4 地址。");

        uint lanGateway = 0;
        if (string.IsNullOrWhiteSpace(lan.Gateway))
            errors.Add("请填写内网网关。");
        else if (!Ipv4.TryParseGateway(lan.Gateway, out lanGateway))
            errors.Add($"内网网关“{lan.Gateway}”不是有效的 IPv4 地址。");

        var networks = request.Networks.Distinct().ToList();
        if (networks.Count == 0) errors.Add("请至少填写一个内网目标网段。");
        foreach (var network in networks.Where(n => n.PrefixLength == 0))
            errors.Add($"目标网段 {network} 无效：默认路由由外网网卡负责。");

        // 外网网关留空：仅当该网卡上已有“在链路上”的默认路由时才允许（如拨号连接）
        var defaults = current.Where(r => r.IsDefault).ToList();
        var wanHop = wanGatewayGiven ? Ipv4.Format(wanGateway) : "0.0.0.0";
        var existingWanDefault = defaults.FirstOrDefault(r => r.IsOn(wan) && r.NextHop == wanHop);
        if (!wanGatewayGiven && existingWanDefault is null)
            errors.Add("外网网卡未检测到默认网关，请手动填写外网网关。");

        if (errors.Count > 0) return new RoutePlan { Errors = errors };

        var lanHop = Ipv4.Format(lanGateway);
        var toAdd = new List<RouteEntry>();
        var toDelete = new List<RouteEntry>();
        var kept = new List<RouteEntry>();
        var toBackup = new List<RouteEntry>(defaults);
        var desired = new List<RouteEntry>();

        // ---------- 默认路由：只保留“外网网卡 + 外网网关”这一条 ----------
        if (existingWanDefault is not null)
        {
            kept.Add(existingWanDefault);
            desired.Add(existingWanDefault);
        }
        else
        {
            var route = NewRoute("0.0.0.0", 0, wanHop, wan);
            toAdd.Add(route);
            desired.Add(route);
        }

        toDelete.AddRange(defaults.Where(r => !ReferenceEquals(r, existingWanDefault)));

        // ---------- 内网网段：每个网段一条指向内网网关的路由 ----------
        foreach (var network in networks)
        {
            var samePrefix = current.Where(r => r.HasPrefix(network)).ToList();
            var existing = samePrefix.FirstOrDefault(r => r.IsOn(lan) && r.NextHop == lanHop);
            if (existing is not null)
            {
                kept.Add(existing);
                desired.Add(existing);
            }
            else
            {
                var route = NewRoute(network.AddressText, network.PrefixLength, lanHop, lan);
                toAdd.Add(route);
                desired.Add(route);
            }

            // 同前缀、走其他网关的路由会与新路由竞争，备份后删除。
            // “在链路上”的路由由系统根据网卡地址自动生成，不备份也不删除。
            foreach (var other in samePrefix.Where(r => !ReferenceEquals(r, existing)))
            {
                if (other.IsOnLink)
                {
                    if (!other.IsOn(lan))
                        warnings.Add($"网段 {network} 在 {other.InterfaceText} 上已有同前缀的在链路路由，实际走哪块网卡取决于跃点数。");
                    continue;
                }

                toBackup.Add(other);
                toDelete.Add(other);
            }
        }

        // ---------- 上次配置遗留、这次不再需要的路由 ----------
        foreach (var old in previouslyAdded)
        {
            var live = current.FirstOrDefault(r => r.SameRoute(old));
            if (live is null) continue;
            if (desired.Any(d => d.SameRoute(live)) || toDelete.Any(d => d.SameRoute(live))) continue;
            toDelete.Add(live);
        }

        AddSubnetWarnings(request, networks, wanGatewayGiven ? wanGateway : null, lanGateway, warnings);

        return new RoutePlan
        {
            ToBackup = toBackup,
            ToAdd = toAdd,
            ToDelete = toDelete,
            Kept = kept,
            Warnings = warnings,
        };
    }

    private static RouteEntry NewRoute(string destination, int prefixLength, string nextHop, RouteEndpoint endpoint) => new()
    {
        Destination = destination,
        PrefixLength = prefixLength,
        NextHop = nextHop,
        InterfaceIndex = endpoint.InterfaceIndex,
        InterfaceLuid = endpoint.InterfaceLuid,
        InterfaceName = endpoint.InterfaceName,
        Metric = RouteMetric,
        Origin = RouteOrigin.Manual,
    };

    private static void AddSubnetWarnings(
        RouteRequest request, List<Ipv4Network> networks, uint? wanGateway, uint lanGateway, List<string> warnings)
    {
        var wanSubnet = SubnetOf(request.Wan);
        var lanSubnet = SubnetOf(request.Lan);

        if (wanGateway is { } wanGw && wanSubnet is { } ws && !ws.Contains(wanGw))
            warnings.Add($"外网网关 {Ipv4.Format(wanGw)} 不在外网网卡所在子网 {ws} 内，路由可能无法生效。");

        if (lanSubnet is { } ls && !ls.Contains(lanGateway))
            warnings.Add($"内网网关 {Ipv4.Format(lanGateway)} 不在内网网卡所在子网 {ls} 内，路由可能无法生效。");

        // 目标网段与外网子网重叠且同样或更具体时，会盖过外网网卡的本地子网路由
        if (wanSubnet is { } subnet)
        {
            foreach (var network in networks.Where(n => n.Overlaps(subnet) && n.PrefixLength >= subnet.PrefixLength))
                warnings.Add($"目标网段 {network} 落在外网网卡所在子网 {subnet} 内，其中的地址将改走内网网卡。");
        }
    }

    private static Ipv4Network? SubnetOf(RouteEndpoint endpoint) =>
        endpoint.LocalPrefixLength is > 0 and <= 32 && Ipv4.TryParse(endpoint.LocalAddress, out var ip)
            ? new Ipv4Network(ip & Ipv4.Mask(endpoint.LocalPrefixLength), endpoint.LocalPrefixLength)
            : null;
}
