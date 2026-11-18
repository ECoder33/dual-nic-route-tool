using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

/// <summary>对照实际路由表，判断外网 / 内网两条线路是否已按用户的选择生效。纯函数。</summary>
public static class RouteStatusEvaluator
{
    public static RouteStatus Evaluate(
        RouteEndpoint? wan,
        RouteEndpoint? lan,
        IReadOnlyList<Ipv4Network> networks,
        IReadOnlyList<RouteEntry> routes)
    {
        var defaults = routes.Where(r => r.IsDefault).ToList();

        // ---------- 外网：默认路由是否只走外网网卡 ----------
        var wanState = PathState.Inactive;
        string wanDetail;
        if (wan is null)
        {
            wanDetail = "未选择外网网卡";
        }
        else
        {
            var gateway = Ipv4.Normalize(wan.Gateway);
            var mine = defaults.Where(r => r.IsOn(wan) && (gateway is null || r.NextHop == gateway)).ToList();
            var others = defaults.Count - mine.Count;

            if (mine.Count > 0 && others == 0)
            {
                wanState = PathState.Active;
                wanDetail = $"默认路由 → {mine[0].NextHopText}";
            }
            else if (mine.Count > 0)
            {
                wanState = PathState.Partial;
                wanDetail = $"另有 {others} 条默认路由在竞争";
            }
            else
            {
                wanDetail = defaults.Count == 0 ? "当前没有默认路由" : "默认路由未指向此网卡";
            }
        }

        // ---------- 内网：目标网段是否都指向内网网关 ----------
        var lanState = PathState.Inactive;
        string lanDetail;
        var networkActive = new Dictionary<Ipv4Network, bool>();
        if (lan is null)
        {
            lanDetail = "未选择内网网卡";
            foreach (var network in networks) networkActive[network] = false;
        }
        else
        {
            var gateway = Ipv4.Normalize(lan.Gateway);
            foreach (var network in networks)
            {
                networkActive[network] = routes.Any(r =>
                    r.HasPrefix(network) && r.IsOn(lan) && !r.IsOnLink && (gateway is null || r.NextHop == gateway));
            }

            var active = networkActive.Values.Count(v => v);
            if (networks.Count == 0)
            {
                lanDetail = "未填写目标网段";
            }
            else if (active == networks.Count)
            {
                lanState = PathState.Active;
                lanDetail = $"{active} 个网段 → {gateway ?? "内网网关"}";
            }
            else if (active > 0)
            {
                lanState = PathState.Partial;
                lanDetail = $"{active}/{networks.Count} 个网段已生效";
            }
            else
            {
                lanDetail = $"{networks.Count} 个网段尚未配置";
            }
        }

        return new RouteStatus
        {
            Wan = wanState,
            WanDetail = wanDetail,
            Lan = lanState,
            LanDetail = lanDetail,
            NetworkActive = networkActive,
        };
    }
}
