using DualNicRouteTool.Models;
using DualNicRouteTool.Services;

namespace DualNicRouteTool.ViewModels;

/// <summary>一个内网目标网段标签。</summary>
public sealed class NetworkChipViewModel(Ipv4Network network) : ObservableObject
{
    private bool _isActive;
    private string _toolTip = string.Empty;

    public Ipv4Network Network { get; } = network;

    public string Text { get; } = network.ToString();

    /// <summary>路由表里是否已有这个网段指向内网网卡的路由。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        set => SetProperty(ref _toolTip, value);
    }
}

/// <summary>进度条上的一个步骤。</summary>
public sealed class StepViewModel(string label) : ObservableObject
{
    private StageState _state;

    public string Label { get; } = label;

    public StageState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

/// <summary>“当前路由”列表里的一行。</summary>
/// <param name="Role">Wan / Lan / 空：这条路由走的是外网网卡、内网网卡还是其他网卡。</param>
public sealed record RouteRowViewModel(
    string Prefix,
    string Gateway,
    string Interface,
    string Metric,
    string Origin,
    string Role,
    string Kind,
    bool AddedByTool);

/// <summary>“备份历史”列表里的一行。</summary>
public sealed class SnapshotViewModel
{
    public SnapshotViewModel(RouteSnapshot snapshot, DateTime bootTimeUtc)
    {
        Snapshot = snapshot;
        IsStale = snapshot.IsStale(bootTimeUtc);
        TimeText = snapshot.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        CanRestore = snapshot.IsRestorable;

        (StatusText, Tone) = snapshot.Status switch
        {
            _ when IsStale && snapshot.IsRestorable => ("已过期", string.Empty),
            SnapshotStatus.Applied => ("可恢复", "Success"),
            SnapshotStatus.Pending => ("配置未完成", "Warning"),
            SnapshotStatus.RestoreFailed => ("恢复未完成", "Error"),
            SnapshotStatus.Restored => ("已恢复", string.Empty),
            SnapshotStatus.Superseded => ("已合并", string.Empty),
            SnapshotStatus.RolledBack => ("已回滚", string.Empty),
            _ => (snapshot.Status.ToString(), string.Empty),
        };

        StatusHint = snapshot.Status switch
        {
            _ when IsStale && snapshot.IsRestorable => "创建于系统上次重启之前。本工具添加的是临时路由，重启时已自动清除。",
            SnapshotStatus.Applied => "配置已生效，可随时恢复到配置前的状态。",
            SnapshotStatus.Pending => "配置途中程序被关闭。可以用它恢复到配置前的状态。",
            SnapshotStatus.RestoreFailed => "上次恢复时有路由未能还原，可以重试。",
            SnapshotStatus.Restored => "已用它恢复过路由。",
            SnapshotStatus.Superseded => "之后又配置过一次，内容已并入更新的备份。",
            SnapshotStatus.RolledBack => "那次配置失败，已自动回滚。",
            _ => string.Empty,
        };

        var wan = snapshot.Wan is { } w ? $"外网 {w.InterfaceName} → {w.Gateway ?? "在链路上"}" : "外网 ?";
        var lan = snapshot.Lan is { } l ? $"内网 {l.InterfaceName} → {l.Gateway}" : "内网 ?";
        Summary = $"{wan}    {lan}";
        Detail = $"{snapshot.Networks.Count} 个网段 · 原始路由 {snapshot.Original.Count} 条 · 本工具新增 {snapshot.Added.Count} 条";
    }

    public RouteSnapshot Snapshot { get; }

    public string TimeText { get; }

    public string StatusText { get; }

    public string StatusHint { get; }

    /// <summary>徽标色调，对应 BadgeStyle 的 Tag。</summary>
    public string Tone { get; }

    public string Summary { get; }

    public string Detail { get; }

    public bool CanRestore { get; }

    public bool IsStale { get; }
}

/// <summary>变更预览对话框的内容。</summary>
public sealed class PlanPreviewModel(RouteRequest request, RoutePlan plan, bool mergesPrevious, bool remoteSession)
{
    public string WanText { get; } =
        $"{request.Wan.InterfaceName}  →  {request.Wan.Gateway ?? "沿用现有的在链路默认路由"}";

    public string LanText { get; } = $"{request.Lan.InterfaceName}  →  {request.Lan.Gateway}";

    public string NetworksText { get; } = string.Join("    ", request.Networks);

    public IReadOnlyList<string> Adds { get; } = plan.ToAdd.Select(r => r.Describe()).ToList();

    public IReadOnlyList<string> Deletes { get; } = plan.ToDelete.Select(r => r.Describe()).ToList();

    public IReadOnlyList<string> Kept { get; } = plan.Kept.Select(r => r.Describe()).ToList();

    public IReadOnlyList<string> Warnings { get; } = plan.Warnings;

    public int BackupCount { get; } = plan.ToBackup.Count;

    /// <summary>上一次配置尚未恢复，本次会沿用最初的原始路由。</summary>
    public bool MergesPrevious { get; } = mergesPrevious;

    /// <summary>当前是远程桌面会话：改默认路由可能切断连接。</summary>
    public bool RemoteSession { get; } = remoteSession;
}

public enum DialogKind
{
    Info,
    Question,
    Warning,
    Error,
}

/// <summary>ViewModel 需要的对话框能力，由视图层实现。</summary>
public interface IDialogService
{
    bool ConfirmPlan(PlanPreviewModel model);

    bool Confirm(string title, string message, string confirmText, DialogKind kind = DialogKind.Question);

    void Show(string title, string message, DialogKind kind = DialogKind.Info);

    void ShowHelp();

    void ShowDiagnostics(string report);
}
