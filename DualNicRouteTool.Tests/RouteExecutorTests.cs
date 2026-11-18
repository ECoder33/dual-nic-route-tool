using DualNicRouteTool.Models;
using DualNicRouteTool.Services;

namespace DualNicRouteTool.Tests;

public class RouteExecutorTests
{
    private static readonly string[] Networks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    [Fact]
    public async Task Apply_ProducesExpectedRoutes_WithoutEverLosingTheDefaultRoute()
    {
        var table = Scenario.Table();
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);

        var result = await executor.ApplyAsync(executor.Prepare(Scenario.Request(Networks)), Scenario.Guard);

        Assert.True(result.Success);
        Assert.True(result.Status!.FullyActive);
        Assert.Single(table.Routes, r => r.IsDefault);
        Assert.Equal(Scenario.WanLuid, table.Routes.Single(r => r.IsDefault).InterfaceLuid);
        foreach (var network in Networks)
            Assert.Single(table.Routes, r => r.Prefix == network && r.NextHop == "10.1.1.1" && r.InterfaceLuid == Scenario.LanLuid);
        Assert.True(table.MinDefaultRoutesSeen >= 1);

        var snapshot = Assert.Single(store.Snapshots);
        Assert.Equal(SnapshotStatus.Applied, snapshot.Status);
        Assert.Equal(2, snapshot.Original.Count);
        Assert.Equal(3, snapshot.Added.Count);
    }

    [Fact]
    public async Task Restore_ReturnsToTheExactOriginalTable_AndLeavesOnLinkRoutesAlone()
    {
        var table = Scenario.Table();
        var before = Scenario.Fingerprint(table);
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);
        var applied = await executor.ApplyAsync(executor.Prepare(Scenario.Request(Networks)), Scenario.Guard);

        var restore = executor.Restore(applied.Snapshot!);

        Assert.True(restore.Success);
        Assert.Equal(1, restore.Restored);
        Assert.Equal(3, restore.Removed);
        // 跃点数也必须原样还原（原 Python 版会把“路由跃点 + 接口跃点”写回去，越恢复越大）
        Assert.Equal(before, Scenario.Fingerprint(table));
        Assert.Equal(SnapshotStatus.Restored, applied.Snapshot!.Status);
    }

    [Fact]
    public async Task Apply_IgnoresStaleDefaultRouteOnADisconnectedAdapter()
    {
        // 真机上遇到的情况：第三块网卡已断开，但系统还留着它的默认路由记录。
        // 它不参与选路，不应被当成冲突删除，也不应进入备份。
        const ulong staleLuid = 0x0800;
        var stale = Scenario.WanDefault with { NextHop = "172.20.0.1", InterfaceIndex = 8, InterfaceLuid = staleLuid, InterfaceName = "WLAN 2" };
        var table = Scenario.Table();
        table.Routes.Add(stale);
        table.Disconnected.Add(staleLuid);
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);

        var context = executor.Prepare(Scenario.Request(Networks));
        var result = await executor.ApplyAsync(context, Scenario.Guard);

        Assert.True(result.Success);
        Assert.True(result.Status!.FullyActive);
        Assert.DoesNotContain(context.Plan.ToDelete, r => r.InterfaceLuid == staleLuid);
        Assert.Contains(table.Routes, r => r.SameRoute(stale));
        Assert.DoesNotContain(result.Snapshot!.Original, r => r.InterfaceLuid == staleLuid);
    }

    [Fact]
    public async Task Restore_AfterLanAdapterDisconnects_StillRemovesTheRoutesItAdded()
    {
        var table = Scenario.Table();
        var store = new FakeBackupStore();
        var (executor, log) = Scenario.Executor(table, store);
        var applied = await executor.ApplyAsync(executor.Prepare(Scenario.Request(Networks)), Scenario.Guard);

        // 拔掉内网网线：内网网卡上的路由不再生效，但记录还留在系统里
        table.Disconnected.Add(Scenario.LanLuid);
        var restore = executor.Restore(applied.Snapshot!);

        // 本工具添加的三条必须清掉，否则网线插回去后它们会重新生效
        Assert.True(restore.Success);
        Assert.Equal(3, restore.Removed);
        Assert.DoesNotContain(table.Routes, r => Networks.Contains(r.Prefix));
        // 内网网卡原来的默认路由无从恢复（网卡未连接），给出提示而不是报错
        Assert.Equal(0, restore.Restored);
        Assert.Contains(log, l => l.Level == LogLevel.Warning && l.Message.Contains("未连接"));
    }

    [Fact]
    public async Task ApplyTwice_ThenRestore_ReturnsToTheVeryFirstState()
    {
        var table = Scenario.Table();
        var before = Scenario.Fingerprint(table);
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);

        await executor.ApplyAsync(executor.Prepare(Scenario.Request(Networks)), Scenario.Guard);
        var second = await executor.ApplyAsync(executor.Prepare(Scenario.Request("10.20.0.0/16")), Scenario.Guard);

        // 第二次配置换了网段：第一次添加的三条应被清掉，只留新的一条
        Assert.True(second.Success);
        Assert.DoesNotContain(table.Routes, r => Networks.Contains(r.Prefix));
        Assert.Single(table.Routes, r => r.Prefix == "10.20.0.0/16");
        Assert.Equal(new[] { SnapshotStatus.Applied, SnapshotStatus.Superseded }, store.Snapshots.Select(s => s.Status).ToArray());

        var restore = executor.Restore(second.Snapshot!);

        Assert.True(restore.Success);
        Assert.Equal(before, Scenario.Fingerprint(table));
    }

    [Fact]
    public async Task SwappingRolesOnSecondApply_StillRestoresToOriginal()
    {
        var table = Scenario.Table();
        var before = Scenario.Fingerprint(table);
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);
        await executor.ApplyAsync(executor.Prepare(Scenario.Request("10.0.0.0/8")), Scenario.Guard);

        // 第二次把内外网对调：以太网作外网，WLAN 作内网
        var swapped = new RouteRequest(
            new RouteEndpoint(Scenario.LanIndex, Scenario.LanLuid, "以太网", "10.1.1.1", "10.1.1.50", 24),
            new RouteEndpoint(Scenario.WanIndex, Scenario.WanLuid, "WLAN", "192.168.1.1", "192.168.1.23", 24),
            [Ipv4Network.Parse("192.168.0.0/16").Network]);
        var second = await executor.ApplyAsync(executor.Prepare(swapped), Scenario.Guard);

        Assert.True(second.Success);
        Assert.Equal(Scenario.LanLuid, table.Routes.Single(r => r.IsDefault).InterfaceLuid);

        Assert.True(executor.Restore(second.Snapshot!).Success);
        Assert.Equal(before, Scenario.Fingerprint(table));
    }

    [Fact]
    public async Task FailureMidway_RollsBackToThePreApplyState()
    {
        var table = Scenario.Table();
        var before = Scenario.Fingerprint(table);
        var store = new FakeBackupStore();
        var (executor, log) = Scenario.Executor(table, store);
        table.FailAdd = route => route.Prefix == "172.16.0.0/12";

        var result = await executor.ApplyAsync(executor.Prepare(Scenario.Request(Networks)), Scenario.Guard);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal(before, Scenario.Fingerprint(table));
        Assert.Equal(SnapshotStatus.RolledBack, Assert.Single(store.Snapshots).Status);
        Assert.Contains(log, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task BackupCannotBeWritten_NothingIsChanged()
    {
        var table = Scenario.Table();
        var before = Scenario.Fingerprint(table);
        var store = new FakeBackupStore { FailSave = true };
        var (executor, _) = Scenario.Executor(table, store);

        var result = await executor.ApplyAsync(executor.Prepare(Scenario.Request(Networks)), Scenario.Guard);

        Assert.False(result.Success);
        Assert.False(result.RolledBack);
        Assert.Equal(before, Scenario.Fingerprint(table));
    }

    [Fact]
    public async Task Guard_RemovesLanDefaultReinstalledByDhcp()
    {
        var table = Scenario.Table();
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);
        var context = executor.Prepare(Scenario.Request(Networks));

        // 配置完成后，每次读路由表前 DHCP 都把内网默认路由装回来一次（共两次）
        var revivals = 2;
        var armed = false;
        table.BeforeRead = t =>
        {
            if (!armed || revivals == 0 || t.Routes.Any(r => r.SameRoute(Scenario.LanDefault))) return;
            revivals--;
            t.Routes.Add(Scenario.LanDefault);
        };
        var progress = new SyncProgress(p => armed |= p is { Stage: ApplyStage.Guard, State: StageState.Active });

        var result = await executor.ApplyAsync(context, new GuardOptions(5, TimeSpan.FromSeconds(1)), progress);

        Assert.True(result.Success);
        Assert.Equal(0, revivals);
        Assert.Single(table.Routes, r => r.IsDefault);
        Assert.True(result.Status!.FullyActive);
    }

    [Fact]
    public void Guard_DistinguishesInterfaces_WhenGatewayAddressesAreIdentical()
    {
        var wanDefault = Scenario.Route("0.0.0.0/0", "192.168.1.1", onWan: true);
        var lanDefault = Scenario.Route("0.0.0.0/0", "192.168.1.1", onWan: false);
        var table = new FakeRouteTable();
        table.Routes.AddRange([wanDefault, lanDefault]);
        var (executor, _) = Scenario.Executor(table, new FakeBackupStore());

        var result = executor.GuardOnce(Scenario.Wan("192.168.1.1"), snapshot: null);

        Assert.Equal(1, result.Cleaned);
        Assert.Equal(Scenario.WanLuid, Assert.Single(table.Routes).InterfaceLuid);
    }

    [Fact]
    public void Guard_DoesNothing_WhenWanDefaultRouteIsMissing()
    {
        // 外网掉线：此时不应把仅剩的默认路由也删掉
        var table = new FakeRouteTable();
        table.Routes.Add(Scenario.LanDefault);
        var (executor, _) = Scenario.Executor(table, new FakeBackupStore());

        var result = executor.GuardOnce(Scenario.Wan(), snapshot: null);

        Assert.True(result.Skipped);
        Assert.Single(table.Routes);
    }

    [Fact]
    public async Task Guard_RecordsUnknownDefaultRoutesInTheBackup_BeforeRemovingThem()
    {
        var table = Scenario.Table();
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);
        var applied = await executor.ApplyAsync(executor.Prepare(Scenario.Request("10.0.0.0/8")), new GuardOptions(0, TimeSpan.Zero));

        // 配置之后才出现的、备份里没有的默认路由
        var late = Scenario.Route("0.0.0.0/0", "10.1.1.254", onWan: false);
        table.Routes.Add(late);
        executor.GuardOnce(Scenario.Wan(), applied.Snapshot);

        Assert.Contains(applied.Snapshot!.Original, r => r.SameRoute(late));
        Assert.True(executor.Restore(applied.Snapshot).Success);
        Assert.Contains(table.Routes, r => r.SameRoute(late));
    }

    [Fact]
    public async Task Restore_KeepsBackup_WhenSomethingFails()
    {
        var table = Scenario.Table();
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);
        var applied = await executor.ApplyAsync(executor.Prepare(Scenario.Request("10.0.0.0/8")), Scenario.Guard);
        table.FailAdd = route => route.IsDefault;

        var failed = executor.Restore(applied.Snapshot!);

        Assert.False(failed.Success);
        Assert.Equal(SnapshotStatus.RestoreFailed, applied.Snapshot!.Status);
        Assert.Contains(applied.Snapshot, store.Snapshots);
        Assert.True(applied.Snapshot.IsRestorable);

        // 故障排除后重试即可成功
        table.FailAdd = null;
        Assert.True(executor.Restore(applied.Snapshot).Success);
        Assert.Contains(table.Routes, r => r.SameRoute(Scenario.LanDefault));
    }

    [Fact]
    public async Task Restore_SkipsRoutesWhoseAdapterIsGone()
    {
        var table = Scenario.Table();
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);
        var applied = await executor.ApplyAsync(executor.Prepare(Scenario.Request("10.0.0.0/8")), Scenario.Guard);

        // 内网网卡被拔掉：它的所有路由随之消失
        table.Routes.RemoveAll(r => r.InterfaceLuid == Scenario.LanLuid);
        var result = executor.Restore(applied.Snapshot!);

        Assert.True(result.Success);
        Assert.DoesNotContain(table.Routes, r => r.InterfaceLuid == Scenario.LanLuid);
    }

    [Fact]
    public async Task StaleBackupFromBeforeReboot_IsNotMergedIntoANewOne()
    {
        var table = Scenario.Table();
        var store = new FakeBackupStore();
        var (executor, _) = Scenario.Executor(table, store);
        await executor.ApplyAsync(executor.Prepare(Scenario.Request("10.0.0.0/8")), Scenario.Guard);

        // 重启之后：临时路由消失，DHCP 重新下发默认路由，启动时间变了
        var rebooted = Scenario.Table();
        var log = new List<(LogLevel, string)>();
        var afterReboot = new RouteExecutor(rebooted, store, (l, m) => log.Add((l, m)),
            delay: (_, _) => Task.CompletedTask, bootTimeUtc: () => Scenario.Boot.AddHours(9));

        var context = afterReboot.Prepare(Scenario.Request("10.0.0.0/8"));

        Assert.Null(context.Active);
        Assert.Single(context.Plan.ToAdd);
    }

    [Fact]
    public void BackupStore_RoundTripsSnapshotsThroughJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DualNicRouteTool.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BackupStore(directory);
            var snapshot = new RouteSnapshot
            {
                Id = "20261118_150000_000",
                CreatedUtc = new DateTime(2026, 11, 18, 7, 0, 0, DateTimeKind.Utc),
                BootTimeUtc = Scenario.Boot,
                Status = SnapshotStatus.Applied,
                Wan = Scenario.Wan(),
                Lan = Scenario.Lan(),
                Networks = ["10.0.0.0/8"],
                Original = [Scenario.WanDefault, Scenario.LanDefault],
                Added = [Scenario.Route("10.0.0.0/8", "10.1.1.1", onWan: false, metric: 1, origin: RouteOrigin.Manual)],
            };

            store.Save(snapshot);
            var loaded = Assert.Single(store.LoadAll());

            Assert.Equal(snapshot.Id, loaded.Id);
            Assert.Equal(SnapshotStatus.Applied, loaded.Status);
            Assert.Equal("以太网", loaded.Lan!.InterfaceName);
            Assert.Equal(Scenario.LanLuid, loaded.Lan.InterfaceLuid);
            Assert.Equal(snapshot.Original, loaded.Original);
            Assert.Equal(snapshot.Added, loaded.Added);
            Assert.Equal(loaded.Id, store.FindActive(Scenario.Boot)!.Id);
            Assert.Null(store.FindActive(Scenario.Boot.AddDays(1)));
            Assert.Contains("以太网", File.ReadAllText(loaded.FilePath!));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>同步回调的进度报告（Progress&lt;T&gt; 会切到线程池，时序不确定）。</summary>
    private sealed class SyncProgress(Action<StageProgress> handler) : IProgress<StageProgress>
    {
        public void Report(StageProgress value) => handler(value);
    }
}
