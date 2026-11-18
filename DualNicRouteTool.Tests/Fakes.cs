using DualNicRouteTool.Models;
using DualNicRouteTool.Native;
using DualNicRouteTool.Services;

namespace DualNicRouteTool.Tests;

/// <summary>内存中的路由表。测试只操作它，绝不触碰系统的真实路由。</summary>
internal sealed class FakeRouteTable : IRouteTable
{
    public List<RouteEntry> Routes { get; } = [];

    /// <summary>让匹配的新增操作失败，用于模拟配置中途出错。</summary>
    public Func<RouteEntry, bool>? FailAdd { get; set; }

    /// <summary>每次读取路由表前调用，用于模拟 DHCP 把路由装回来。</summary>
    public Action<FakeRouteTable>? BeforeRead { get; set; }

    /// <summary>历史上出现过的最少默认路由条数，用于验证不存在“没有默认路由”的空窗。</summary>
    public int MinDefaultRoutesSeen { get; private set; } = int.MaxValue;

    /// <summary>
    /// 已断开的网卡（按 LUID）。与真实系统一致：它们的路由记录还留在表里、仍可增删，
    /// 但不参与选路，<see cref="GetRoutes"/> 不返回。
    /// </summary>
    public HashSet<ulong> Disconnected { get; } = [];

    public IReadOnlyList<RouteEntry> GetRoutes()
    {
        BeforeRead?.Invoke(this);
        return Routes.Where(r => !Disconnected.Contains(r.InterfaceLuid)).ToList();
    }

    public IReadOnlyList<RouteEntry> GetInactiveRoutes() =>
        Routes.Where(r => Disconnected.Contains(r.InterfaceLuid)).ToList();

    public void Add(RouteEntry route)
    {
        if (FailAdd?.Invoke(route) == true)
            throw new RouteOperationException($"添加路由 {route.Prefix}", 87);
        if (Routes.Any(r => r.SameRoute(route)))
            throw new RouteOperationException($"添加路由 {route.Prefix}", IpHelper.ERROR_OBJECT_ALREADY_EXISTS);
        Routes.Add(route);
        Track();
    }

    public void Delete(RouteEntry route)
    {
        var index = Routes.FindIndex(r => r.SameRoute(route));
        if (index < 0)
            throw new RouteOperationException($"删除路由 {route.Prefix}", IpHelper.ERROR_NOT_FOUND);
        Routes.RemoveAt(index);
        Track();
    }

    private void Track() => MinDefaultRoutesSeen = Math.Min(MinDefaultRoutesSeen, Routes.Count(r => r.IsDefault));
}

/// <summary>内存中的备份存储。</summary>
internal sealed class FakeBackupStore : IBackupStore
{
    public List<RouteSnapshot> Snapshots { get; } = [];

    public bool FailSave { get; set; }

    public string Directory => "memory";

    public void Save(RouteSnapshot snapshot)
    {
        if (FailSave) throw new IOException("磁盘已满");
        snapshot.FilePath ??= $"memory/{snapshot.Id}.json";
        if (!Snapshots.Contains(snapshot)) Snapshots.Insert(0, snapshot);
    }

    public IReadOnlyList<RouteSnapshot> LoadAll() => Snapshots.ToList();

    public void Delete(RouteSnapshot snapshot) => Snapshots.Remove(snapshot);
}

/// <summary>一套典型的双网卡环境：Wi-Fi 作外网，以太网作内网，两边都由 DHCP 下发了默认路由。</summary>
internal static class Scenario
{
    public const uint WanIndex = 12;
    public const ulong WanLuid = 0x1200;
    public const uint LanIndex = 7;
    public const ulong LanLuid = 0x0700;

    public static readonly DateTime Boot = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);

    public static RouteEndpoint Wan(string? gateway = "192.168.1.1") =>
        new(WanIndex, WanLuid, "WLAN", gateway, "192.168.1.23", 24);

    public static RouteEndpoint Lan(string? gateway = "10.1.1.1") =>
        new(LanIndex, LanLuid, "以太网", gateway, "10.1.1.50", 24);

    public static RouteRequest Request(params string[] networks) =>
        new(Wan(), Lan(), networks.Select(n => Ipv4Network.Parse(n).Network).ToList());

    public static RouteEntry Route(string prefix, string nextHop, bool onWan, uint metric = 0, RouteOrigin origin = RouteOrigin.Dhcp)
    {
        var network = prefix.Split('/');
        return new RouteEntry
        {
            Destination = network[0],
            PrefixLength = int.Parse(network[1]),
            NextHop = nextHop,
            InterfaceIndex = onWan ? WanIndex : LanIndex,
            InterfaceLuid = onWan ? WanLuid : LanLuid,
            InterfaceName = onWan ? "WLAN" : "以太网",
            Metric = metric,
            Origin = origin,
        };
    }

    public static RouteEntry WanDefault => Route("0.0.0.0/0", "192.168.1.1", onWan: true);

    public static RouteEntry LanDefault => Route("0.0.0.0/0", "10.1.1.1", onWan: false);

    /// <summary>系统根据网卡地址自动生成的在链路路由。</summary>
    public static RouteEntry[] OnLinkRoutes =>
    [
        Route("192.168.1.0/24", "0.0.0.0", onWan: true, metric: 256, origin: RouteOrigin.WellKnown),
        Route("192.168.1.23/32", "0.0.0.0", onWan: true, metric: 256, origin: RouteOrigin.WellKnown),
        Route("10.1.1.0/24", "0.0.0.0", onWan: false, metric: 256, origin: RouteOrigin.WellKnown),
        Route("10.1.1.50/32", "0.0.0.0", onWan: false, metric: 256, origin: RouteOrigin.WellKnown),
    ];

    public static FakeRouteTable Table()
    {
        var table = new FakeRouteTable();
        table.Routes.AddRange([WanDefault, LanDefault, .. OnLinkRoutes]);
        return table;
    }

    public static (RouteExecutor Executor, List<(LogLevel Level, string Message)> Log) Executor(
        FakeRouteTable table, FakeBackupStore store)
    {
        var log = new List<(LogLevel, string)>();
        var executor = new RouteExecutor(
            table, store, (level, message) => log.Add((level, message)),
            delay: (_, _) => Task.CompletedTask,
            bootTimeUtc: () => Boot);
        return (executor, log);
    }

    public static readonly GuardOptions Guard = new(3, TimeSpan.FromSeconds(1));

    /// <summary>路由表内容的规范化表示，用于比较“恢复后是否与最初一致”。</summary>
    public static string[] Fingerprint(FakeRouteTable table) =>
        table.Routes.Select(r => $"{r.Prefix}|{r.NextHop}|{r.InterfaceLuid}|{r.Metric}").Order().ToArray();
}
