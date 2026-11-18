using System.IO;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

public enum ApplyStage
{
    Backup,
    WanRoute,
    LanRoutes,
    Cleanup,
    Guard,
    Verify,
}

public enum StageState
{
    Pending,
    Active,
    Done,
    Failed,
}

public sealed record StageProgress(ApplyStage Stage, StageState State);

/// <param name="Rounds">配置完成后的守护轮次，0 表示不守护。</param>
public sealed record GuardOptions(int Rounds, TimeSpan Interval);

/// <summary>一次配置的准备结果：供界面预览，确认后原样交给 <see cref="RouteExecutor.ApplyAsync"/> 执行。</summary>
/// <param name="Active">上一次配置留下的、尚未恢复的备份。</param>
public sealed record ApplyContext(RouteRequest Request, RoutePlan Plan, RouteSnapshot? Active);

public sealed record ApplyResult(bool Success, bool RolledBack, RouteSnapshot? Snapshot, RouteStatus? Status);

public sealed record RestoreResult(bool Success, int Restored, int Removed, int Skipped, int Failed);

/// <param name="Skipped">外网默认路由不存在，本轮未做清理。</param>
public sealed record GuardResult(int Cleaned, int Failed, bool Skipped);

/// <summary>执行配置、回滚、恢复与守护。</summary>
public sealed class RouteExecutor
{
    private readonly IRouteTable _table;
    private readonly IBackupStore _store;
    private readonly Action<LogLevel, string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _bootTimeUtc;

    public RouteExecutor(
        IRouteTable table,
        IBackupStore store,
        Action<LogLevel, string> log,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? bootTimeUtc = null)
    {
        _table = table;
        _store = store;
        _log = log;
        _delay = delay ?? Task.Delay;
        _bootTimeUtc = bootTimeUtc ?? (() => AppEnvironment.BootTimeUtc);
    }

    /// <summary>读取当前路由表并计算改动，不修改任何东西。</summary>
    public ApplyContext Prepare(RouteRequest request)
    {
        var current = _table.GetRoutes();
        var active = _store.FindActive(_bootTimeUtc());
        return new ApplyContext(request, RoutePlanner.Build(request, current, active?.Added), active);
    }

    // ========================================================================
    // 配置
    // ========================================================================

    public async Task<ApplyResult> ApplyAsync(
        ApplyContext context,
        GuardOptions guard,
        IProgress<StageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (request, plan, active) = context;
        void Report(ApplyStage stage, StageState state) => progress?.Report(new StageProgress(stage, state));

        if (!plan.IsValid)
        {
            foreach (var error in plan.Errors) _log(LogLevel.Error, error);
            return new ApplyResult(false, false, null, null);
        }

        _log(LogLevel.Title, "开始配置双网卡路由");
        _log(LogLevel.Info, $"外网：{request.Wan.InterfaceName}（接口 {request.Wan.InterfaceIndex}），网关 {request.Wan.Gateway ?? "沿用现有的在链路默认路由"}");
        _log(LogLevel.Info, $"内网：{request.Lan.InterfaceName}（接口 {request.Lan.InterfaceIndex}），网关 {request.Lan.Gateway}");
        _log(LogLevel.Info, $"内网目标网段：{string.Join("; ", request.Networks)}");
        foreach (var warning in plan.Warnings) _log(LogLevel.Warning, warning);

        // ---------- 1. 备份：写不进磁盘就不动路由表 ----------
        Report(ApplyStage.Backup, StageState.Active);
        var snapshot = BuildSnapshot(request, plan, active);
        try
        {
            _store.Save(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log(LogLevel.Error, $"无法写入备份（{ex.Message}），已取消配置，未修改任何路由。");
            Report(ApplyStage.Backup, StageState.Failed);
            return new ApplyResult(false, false, null, null);
        }

        _log(LogLevel.Success, $"已备份 {snapshot.Original.Count} 条原始路由 → {snapshot.FilePath}");
        if (active is not null)
            _log(LogLevel.Info, "上一次配置尚未恢复：本次备份沿用最初的原始路由，恢复时会回到最初状态。");
        Report(ApplyStage.Backup, StageState.Done);

        // ---------- 2-4. 先加后删：任何时刻都不会出现“没有默认路由”的空窗 ----------
        // undo 记录本次实际做过的每一步，回滚时按相反顺序撤销（不依赖磁盘上的备份文件）
        var undo = new List<(bool Added, RouteEntry Route)>();
        var stage = ApplyStage.WanRoute;
        try
        {
            Report(stage, StageState.Active);
            foreach (var route in plan.Kept.Where(r => r.IsDefault))
                _log(LogLevel.Info, $"外网默认路由已存在，保持不变：{route.Describe()}");
            foreach (var route in plan.ToAdd.Where(r => r.IsDefault)) AddRoute(route, undo);
            Report(stage, StageState.Done);

            stage = ApplyStage.LanRoutes;
            Report(stage, StageState.Active);
            foreach (var route in plan.Kept.Where(r => !r.IsDefault))
                _log(LogLevel.Info, $"内网路由已存在，保持不变：{route.Describe()}");
            foreach (var route in plan.ToAdd.Where(r => !r.IsDefault)) AddRoute(route, undo);
            Report(stage, StageState.Done);

            stage = ApplyStage.Cleanup;
            Report(stage, StageState.Active);
            foreach (var route in plan.ToDelete) DeleteRoute(route, undo);
            Report(stage, StageState.Done);
        }
        catch (RouteOperationException ex)
        {
            _log(LogLevel.Error, ex.Message);
            Report(stage, StageState.Failed);
            _log(LogLevel.Warning, "配置失败，开始撤销本次改动 ...");

            var clean = Rollback(undo);
            snapshot.Status = clean ? SnapshotStatus.RolledBack : SnapshotStatus.RestoreFailed;
            TrySave(snapshot);
            if (!clean && active is not null)
            {
                active.Status = SnapshotStatus.Superseded;
                TrySave(active);
            }

            if (clean) _log(LogLevel.Info, "已回滚，路由表回到本次配置前的状态。");
            else _log(LogLevel.Error, "回滚未完全成功，请到“备份历史”里手动恢复。");
            return new ApplyResult(false, true, snapshot, null);
        }

        snapshot.Status = SnapshotStatus.Applied;
        TrySave(snapshot);
        if (active is not null)
        {
            active.Status = SnapshotStatus.Superseded;
            TrySave(active);
        }

        // ---------- 5. 守护：DHCP 可能在几秒内把内网默认路由装回来 ----------
        Report(ApplyStage.Guard, StageState.Active);
        if (guard.Rounds > 0)
        {
            _log(LogLevel.Info, $"启动默认路由守护：{guard.Rounds} 轮 × {guard.Interval.TotalSeconds:0.#} 秒（只保留外网网卡上的默认路由）");
            var cleaned = 0;
            try
            {
                for (var round = 1; round <= guard.Rounds; round++)
                {
                    await _delay(guard.Interval, cancellationToken).ConfigureAwait(false);
                    var prefix = $"[第 {round}/{guard.Rounds} 轮] ";
                    var result = GuardOnce(request.Wan, snapshot, prefix);
                    cleaned += result.Cleaned;
                    if (result.Skipped) _log(LogLevel.Warning, prefix + "外网默认路由不存在，跳过清理");
                    else if (result is { Cleaned: 0, Failed: 0 }) _log(LogLevel.Info, prefix + "路由表干净");
                }

                _log(LogLevel.Info, $"守护结束，累计清理 {cleaned} 条非外网默认路由");
            }
            catch (OperationCanceledException)
            {
                _log(LogLevel.Warning, "守护被中止");
            }
        }

        Report(ApplyStage.Guard, StageState.Done);

        // ---------- 6. 校验：以实际路由表为准 ----------
        Report(ApplyStage.Verify, StageState.Active);
        var status = RouteStatusEvaluator.Evaluate(request.Wan, request.Lan, request.Networks, _table.GetRoutes());
        if (status.FullyActive)
        {
            _log(LogLevel.Success, $"校验通过：{status.WanDetail}；{status.LanDetail}");
            Report(ApplyStage.Verify, StageState.Done);
        }
        else
        {
            _log(LogLevel.Warning, $"校验发现路由未完全生效：外网 {status.WanDetail}；内网 {status.LanDetail}");
            Report(ApplyStage.Verify, StageState.Failed);
        }

        _log(LogLevel.Title, "双网卡路由配置完成");
        return new ApplyResult(true, false, snapshot, status);
    }

    // ========================================================================
    // 守护
    // ========================================================================

    /// <summary>
    /// 巡检一次：删除不在外网网卡上的默认路由。按接口判断而不是按网关 IP，
    /// 因此两块网卡的网关地址相同（例如都是 192.168.1.1）时也能区分。
    /// 外网默认路由不存在时不做任何清理，避免在外网掉线时把仅剩的出口删掉。
    /// </summary>
    public GuardResult GuardOnce(RouteEndpoint wan, RouteSnapshot? snapshot, string logPrefix = "")
    {
        var gateway = Ipv4.Normalize(wan.Gateway) ?? "0.0.0.0";
        var defaults = _table.GetRoutes().Where(r => r.IsDefault).ToList();
        bool IsWanDefault(RouteEntry r) => r.IsOn(wan) && r.NextHop == gateway;

        if (!defaults.Any(IsWanDefault)) return new GuardResult(0, 0, true);

        int cleaned = 0, failed = 0;
        foreach (var route in defaults.Where(r => !IsWanDefault(r)))
        {
            // 清理前没见过的默认路由先记入备份，保证之后仍可恢复
            if (snapshot is not null && !snapshot.Original.Any(o => o.SameRoute(route)) && !snapshot.Added.Any(a => a.SameRoute(route)))
            {
                snapshot.Original.Add(route);
                TrySave(snapshot);
            }

            try
            {
                _table.Delete(route);
                cleaned++;
                _log(LogLevel.Success, $"{logPrefix}清理默认路由 → {route.NextHopText} [{route.InterfaceText}]");
            }
            catch (RouteOperationException ex) when (ex.IsNotFound)
            {
            }
            catch (RouteOperationException ex)
            {
                failed++;
                _log(LogLevel.Warning, $"{logPrefix}清理默认路由 → {route.NextHopText} [{route.InterfaceText}] 失败：{ex.Message}");
            }
        }

        return new GuardResult(cleaned, failed, false);
    }

    // ========================================================================
    // 恢复
    // ========================================================================

    /// <summary>
    /// 按备份恢复：先补回缺失的原始路由，再删除本工具新增的路由。
    /// 不触碰其他路由（包括系统自动生成的在链路路由）。有路由未能还原时保留备份以便重试。
    /// </summary>
    public RestoreResult Restore(RouteSnapshot snapshot)
    {
        _log(LogLevel.Title, "开始恢复路由");
        _log(LogLevel.Info, $"使用备份：{snapshot.FilePath ?? snapshot.Id}");

        var current = _table.GetRoutes();
        int restored = 0, removed = 0, skipped = 0, failed = 0;

        foreach (var route in snapshot.Original)
        {
            var live = current.FirstOrDefault(c => c.SameRoute(route));
            if (live is not null)
            {
                // 同一条路由已在表里。只有当它是本工具后来重建的、且跃点数与原始值不同时才换回去
                // （例如第二次配置对调了内外网）；其余情况保持现状
                var rebuiltByTool = snapshot.Added.Any(a => a.SameRoute(route));
                if (!rebuiltByTool || live.Metric == route.Metric)
                {
                    skipped++;
                    _log(LogLevel.Info, $"已存在，跳过：{route.Describe()}");
                    continue;
                }

                try
                {
                    _table.Delete(live);
                }
                catch (RouteOperationException ex) when (ex.IsNotFound)
                {
                }
                catch (RouteOperationException ex)
                {
                    failed++;
                    _log(LogLevel.Error, $"还原 {route.Describe()} 的跃点数失败：{ex.Message}");
                    continue;
                }
            }

            // 接口上没有任何路由，说明网卡已断开或被移除（例如 VPN 已断开），此时无从恢复
            if (!current.Any(c => c.IsOn(route.InterfaceIndex, route.InterfaceLuid)))
            {
                skipped++;
                _log(LogLevel.Warning, $"网卡 {route.InterfaceText} 当前未连接，跳过：{route.Describe()}");
                continue;
            }

            try
            {
                _table.Add(route);
                restored++;
                _log(LogLevel.Success, $"恢复 {route.Describe()}");
            }
            catch (RouteOperationException ex) when (ex.IsAlreadyExists)
            {
                skipped++;
            }
            catch (RouteOperationException ex)
            {
                failed++;
                _log(LogLevel.Error, $"恢复 {route.Describe()} 失败：{ex.Message}");
            }
        }

        foreach (var route in snapshot.Added)
        {
            if (snapshot.Original.Any(o => o.SameRoute(route))) continue;

            // 不先查表而是直接删：网卡断开后这条路由不在“生效路由”里，但记录可能还留在系统中，
            // 网卡重新连上时会再次生效。已经不存在的（重启、网卡被移除）按“找不到”静默跳过。
            try
            {
                _table.Delete(route);
                removed++;
                _log(LogLevel.Success, $"移除本工具添加的路由 {route.Describe()}");
            }
            catch (RouteOperationException ex) when (ex.IsNotFound)
            {
            }
            catch (RouteOperationException ex)
            {
                failed++;
                _log(LogLevel.Error, $"移除 {route.Describe()} 失败：{ex.Message}");
            }
        }

        snapshot.Status = failed == 0 ? SnapshotStatus.Restored : SnapshotStatus.RestoreFailed;
        TrySave(snapshot);

        _log(failed == 0 ? LogLevel.Success : LogLevel.Warning,
            $"恢复结果：还原 {restored} 条，移除 {removed} 条，跳过 {skipped} 条，失败 {failed} 条");
        if (failed > 0) _log(LogLevel.Warning, "有路由未能还原，备份已保留，可稍后重试。");
        _log(LogLevel.Title, "路由恢复完成");

        return new RestoreResult(failed == 0, restored, removed, skipped, failed);
    }

    // ========================================================================
    // 内部
    // ========================================================================

    private RouteSnapshot BuildSnapshot(RouteRequest request, RoutePlan plan, RouteSnapshot? active)
    {
        var previouslyAdded = active?.Added ?? [];

        // 原始路由：沿用上一份备份的内容，再补上这次新发现的（上次由本工具添加的不算原始路由）
        var original = new List<RouteEntry>(active?.Original ?? []);
        foreach (var route in plan.ToBackup)
        {
            if (previouslyAdded.Any(a => a.SameRoute(route))) continue;
            if (original.Any(o => o.SameRoute(route))) continue;
            original.Add(route);
        }

        // 新增路由在动手之前就写进备份：即使程序中途被关闭，恢复时也知道该移除哪些
        var added = new List<RouteEntry>(previouslyAdded);
        foreach (var route in plan.ToAdd)
        {
            if (!added.Any(a => a.SameRoute(route))) added.Add(route);
        }

        var now = DateTime.Now;
        return new RouteSnapshot
        {
            Id = now.ToString("yyyyMMdd_HHmmss_fff"),
            CreatedUtc = now.ToUniversalTime(),
            BootTimeUtc = _bootTimeUtc(),
            Status = SnapshotStatus.Pending,
            AppVersion = AppEnvironment.Version,
            Wan = request.Wan,
            Lan = request.Lan,
            Networks = request.Networks.Select(n => n.ToString()).ToList(),
            Original = original,
            Added = added,
        };
    }

    private void AddRoute(RouteEntry route, List<(bool Added, RouteEntry Route)> undo)
    {
        try
        {
            _table.Add(route);
            undo.Add((true, route));
            _log(LogLevel.Success, $"添加 {route.Describe()}");
        }
        catch (RouteOperationException ex) when (ex.IsAlreadyExists)
        {
            _log(LogLevel.Info, $"已存在，跳过：{route.Describe()}");
        }
    }

    private void DeleteRoute(RouteEntry route, List<(bool Added, RouteEntry Route)> undo)
    {
        try
        {
            _table.Delete(route);
            undo.Add((false, route));
            _log(LogLevel.Success, $"删除 {route.Describe()}");
        }
        catch (RouteOperationException ex) when (ex.IsNotFound)
        {
            _log(LogLevel.Info, $"已不存在，跳过：{route.Describe()}");
        }
    }

    /// <summary>按相反顺序撤销本次已做的改动；全部成功返回 true。</summary>
    private bool Rollback(List<(bool Added, RouteEntry Route)> undo)
    {
        var clean = true;
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            var (added, route) = undo[i];
            try
            {
                if (added)
                {
                    _table.Delete(route);
                    _log(LogLevel.Info, $"撤销：移除 {route.Describe()}");
                }
                else
                {
                    _table.Add(route);
                    _log(LogLevel.Info, $"撤销：还原 {route.Describe()}");
                }
            }
            catch (RouteOperationException ex) when (ex.IsNotFound || ex.IsAlreadyExists)
            {
            }
            catch (RouteOperationException ex)
            {
                clean = false;
                _log(LogLevel.Error, $"撤销失败：{route.Describe()}：{ex.Message}");
            }
        }

        return clean;
    }

    private void TrySave(RouteSnapshot snapshot)
    {
        try
        {
            _store.Save(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log(LogLevel.Warning, $"更新备份文件失败：{ex.Message}");
        }
    }
}
