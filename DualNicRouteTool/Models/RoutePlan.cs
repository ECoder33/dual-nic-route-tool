namespace DualNicRouteTool.Models;

/// <summary>一次配置将对路由表做的全部改动。由 <c>RoutePlanner</c> 计算，供预览、执行和测试共用。</summary>
public sealed class RoutePlan
{
    /// <summary>执行前需要备份的现有路由（全部默认路由 + 与目标网段同前缀的网关路由）。</summary>
    public IReadOnlyList<RouteEntry> ToBackup { get; init; } = [];

    /// <summary>需要新增的路由，外网默认路由排在最前。</summary>
    public IReadOnlyList<RouteEntry> ToAdd { get; init; } = [];

    /// <summary>需要删除的路由。</summary>
    public IReadOnlyList<RouteEntry> ToDelete { get; init; } = [];

    /// <summary>已经符合要求、保持不变的路由。</summary>
    public IReadOnlyList<RouteEntry> Kept { get; init; } = [];

    /// <summary>不阻止执行、但需要用户知情的情况。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>阻止执行的问题。</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsValid => Errors.Count == 0;

    public bool HasChanges => ToAdd.Count > 0 || ToDelete.Count > 0;
}

/// <summary>拓扑图里一条线路的状态。</summary>
public enum PathState
{
    /// <summary>未配置。</summary>
    Inactive,

    /// <summary>部分生效或存在冲突。</summary>
    Partial,

    /// <summary>已完全生效。</summary>
    Active,
}

/// <summary>根据实际路由表得出的生效情况。</summary>
public sealed class RouteStatus
{
    public PathState Wan { get; init; }
    public string WanDetail { get; init; } = string.Empty;
    public PathState Lan { get; init; }
    public string LanDetail { get; init; } = string.Empty;

    /// <summary>每个目标网段是否已指向内网网卡。</summary>
    public IReadOnlyDictionary<Ipv4Network, bool> NetworkActive { get; init; } = new Dictionary<Ipv4Network, bool>();

    public bool FullyActive => Wan == PathState.Active && Lan == PathState.Active;
}
