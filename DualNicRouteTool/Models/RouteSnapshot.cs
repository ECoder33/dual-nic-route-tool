using System.Text.Json.Serialization;

namespace DualNicRouteTool.Models;

public enum SnapshotStatus
{
    /// <summary>配置尚未完成（进行中，或程序在配置途中被关闭）。</summary>
    Pending,

    /// <summary>配置已生效，可恢复。</summary>
    Applied,

    /// <summary>已恢复。</summary>
    Restored,

    /// <summary>恢复时有路由未能还原，备份保留，可重试。</summary>
    RestoreFailed,

    /// <summary>之后又配置过一次，内容已并入更新的备份。</summary>
    Superseded,

    /// <summary>配置失败并已自动回滚。</summary>
    RolledBack,
}

/// <summary>
/// 一次配置对应的备份。记录“配置前的原始路由”和“本工具新增的路由”：
/// 恢复时补回缺失的原始路由，并且只删除本工具新增的路由。
/// </summary>
public sealed class RouteSnapshot
{
    public int FormatVersion { get; set; } = 2;

    public string Id { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// 创建备份时的系统启动时间。本工具添加的是非永久路由，重启后自动消失，
    /// 因此启动时间不同的备份视为已过期。
    /// </summary>
    public DateTime BootTimeUtc { get; set; }

    public SnapshotStatus Status { get; set; }

    public string AppVersion { get; set; } = string.Empty;

    public RouteEndpoint? Wan { get; set; }

    public RouteEndpoint? Lan { get; set; }

    public List<string> Networks { get; set; } = [];

    /// <summary>配置前的原始路由。连续配置多次时沿用第一次的内容，恢复始终回到最初状态。</summary>
    public List<RouteEntry> Original { get; set; } = [];

    /// <summary>本工具新增的路由。</summary>
    public List<RouteEntry> Added { get; set; } = [];

    [JsonIgnore] public string? FilePath { get; set; }

    [JsonIgnore] public bool IsRestorable => Status is SnapshotStatus.Applied or SnapshotStatus.Pending or SnapshotStatus.RestoreFailed;

    /// <summary>备份是否创建于本次开机之前（容忍 2 分钟的系统时钟校正）。</summary>
    public bool IsStale(DateTime currentBootTimeUtc) =>
        Math.Abs((BootTimeUtc - currentBootTimeUtc).TotalMinutes) > 2;
}
