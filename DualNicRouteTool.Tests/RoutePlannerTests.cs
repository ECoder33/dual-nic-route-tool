using DualNicRouteTool.Models;
using DualNicRouteTool.Services;

namespace DualNicRouteTool.Tests;

public class RoutePlannerTests
{
    [Fact]
    public void TypicalDualNic_KeepsWanDefault_RemovesLanDefault_AddsLanRoutes()
    {
        var table = Scenario.Table();

        var plan = RoutePlanner.Build(Scenario.Request("10.0.0.0/8", "172.16.0.0/12"), table.Routes);

        Assert.True(plan.IsValid);
        Assert.Single(plan.Kept, r => r.SameRoute(Scenario.WanDefault));
        Assert.Single(plan.ToDelete);
        Assert.True(plan.ToDelete[0].SameRoute(Scenario.LanDefault));
        Assert.Equal(new[] { "10.0.0.0/8", "172.16.0.0/12" }, plan.ToAdd.Select(r => r.Prefix).ToArray());
        Assert.All(plan.ToAdd, r =>
        {
            Assert.Equal("10.1.1.1", r.NextHop);
            Assert.Equal(Scenario.LanLuid, r.InterfaceLuid);
            Assert.Equal(RoutePlanner.RouteMetric, r.Metric);
        });
    }

    [Fact]
    public void Backup_ContainsOnlyDefaultsAndConflicts_NeverOnLinkRoutes()
    {
        var table = Scenario.Table();
        var conflict = Scenario.Route("10.0.0.0/8", "192.168.1.254", onWan: true, metric: 5, origin: RouteOrigin.Manual);
        table.Routes.Add(conflict);

        var plan = RoutePlanner.Build(Scenario.Request("10.0.0.0/8"), table.Routes);

        Assert.Equal(3, plan.ToBackup.Count);
        Assert.Contains(plan.ToBackup, r => r.SameRoute(Scenario.WanDefault));
        Assert.Contains(plan.ToBackup, r => r.SameRoute(Scenario.LanDefault));
        Assert.Contains(plan.ToBackup, r => r.SameRoute(conflict));
        Assert.DoesNotContain(plan.ToBackup, r => r.IsOnLink);
        Assert.Contains(plan.ToDelete, r => r.SameRoute(conflict));
        Assert.DoesNotContain(plan.ToDelete, r => r.IsOnLink);
    }

    [Fact]
    public void NoWanDefault_AddsOneWithGivenGateway_BeforeLanRoutes()
    {
        var table = Scenario.Table();
        table.Routes.RemoveAll(r => r.SameRoute(Scenario.WanDefault));

        var plan = RoutePlanner.Build(Scenario.Request("10.0.0.0/8"), table.Routes);

        Assert.True(plan.ToAdd[0].IsDefault);
        Assert.Equal("192.168.1.1", plan.ToAdd[0].NextHop);
        Assert.Equal(Scenario.WanLuid, plan.ToAdd[0].InterfaceLuid);
        Assert.Equal("10.0.0.0/8", plan.ToAdd[1].Prefix);
    }

    [Fact]
    public void BothGatewaysShareTheSameAddress_AreDistinguishedByInterface()
    {
        // 两块网卡的网关都是 192.168.1.1：原 Python 版按网关 IP 判断，无法区分
        var wanDefault = Scenario.Route("0.0.0.0/0", "192.168.1.1", onWan: true);
        var lanDefault = Scenario.Route("0.0.0.0/0", "192.168.1.1", onWan: false);
        var request = new RouteRequest(Scenario.Wan("192.168.1.1"), Scenario.Lan("192.168.1.1"),
            [Ipv4Network.Parse("10.0.0.0/8").Network]);

        var plan = RoutePlanner.Build(request, [wanDefault, lanDefault]);

        Assert.Single(plan.Kept);
        Assert.Equal(Scenario.WanLuid, plan.Kept[0].InterfaceLuid);
        Assert.Single(plan.ToDelete);
        Assert.Equal(Scenario.LanLuid, plan.ToDelete[0].InterfaceLuid);
    }

    [Fact]
    public void AlreadyConfigured_HasNoChanges()
    {
        var table = new FakeRouteTable();
        table.Routes.AddRange([Scenario.WanDefault, .. Scenario.OnLinkRoutes,
            Scenario.Route("10.0.0.0/8", "10.1.1.1", onWan: false, metric: 1, origin: RouteOrigin.Manual)]);

        var plan = RoutePlanner.Build(Scenario.Request("10.0.0.0/8"), table.Routes);

        Assert.True(plan.IsValid);
        Assert.False(plan.HasChanges);
        Assert.Equal(2, plan.Kept.Count);
    }

    [Fact]
    public void PreviouslyAddedRoutes_NoLongerWanted_AreRemoved()
    {
        var stale = Scenario.Route("172.16.0.0/12", "10.1.1.1", onWan: false, metric: 1, origin: RouteOrigin.Manual);
        var wanted = Scenario.Route("10.0.0.0/8", "10.1.1.1", onWan: false, metric: 1, origin: RouteOrigin.Manual);
        var table = new FakeRouteTable();
        table.Routes.AddRange([Scenario.WanDefault, stale, wanted]);

        var plan = RoutePlanner.Build(Scenario.Request("10.0.0.0/8"), table.Routes, previouslyAdded: [stale, wanted]);

        Assert.Single(plan.ToDelete);
        Assert.True(plan.ToDelete[0].SameRoute(stale));
        Assert.Empty(plan.ToAdd);
    }

    [Fact]
    public void LanGatewayMayBeEnteredManually_WhenAdapterReportsNone()
    {
        // 内网网卡没有默认路由（系统不上报网关），用户手动填写网关即可配置
        var table = Scenario.Table();
        table.Routes.RemoveAll(r => r.SameRoute(Scenario.LanDefault));

        var plan = RoutePlanner.Build(Scenario.Request("10.0.0.0/8"), table.Routes);

        Assert.True(plan.IsValid);
        Assert.Empty(plan.ToDelete);
        Assert.Single(plan.ToAdd);
    }

    [Fact]
    public void WanWithoutGateway_UsesExistingOnLinkDefault()
    {
        // 拨号连接：默认路由在链路上，没有网关地址
        var pppDefault = Scenario.Route("0.0.0.0/0", "0.0.0.0", onWan: true);
        var request = new RouteRequest(Scenario.Wan(gateway: null), Scenario.Lan(), [Ipv4Network.Parse("10.0.0.0/8").Network]);

        var plan = RoutePlanner.Build(request, [pppDefault, Scenario.LanDefault]);

        Assert.True(plan.IsValid);
        Assert.Single(plan.Kept, r => r.SameRoute(pppDefault));
        Assert.Single(plan.ToDelete, r => r.SameRoute(Scenario.LanDefault));
    }

    [Fact]
    public void Validation_ReportsEveryProblem()
    {
        var sameCard = new RouteRequest(Scenario.Wan(), Scenario.Wan("192.168.1.1"), [Ipv4Network.Parse("10.0.0.0/8").Network]);
        Assert.Contains(RoutePlanner.Build(sameCard, []).Errors, e => e.Contains("同一块网卡"));

        var noLanGateway = new RouteRequest(Scenario.Wan(), Scenario.Lan(gateway: null), [Ipv4Network.Parse("10.0.0.0/8").Network]);
        Assert.Contains(RoutePlanner.Build(noLanGateway, []).Errors, e => e.Contains("内网网关"));

        var badGateway = new RouteRequest(Scenario.Wan("192.168.1.999"), Scenario.Lan(), [Ipv4Network.Parse("10.0.0.0/8").Network]);
        Assert.Contains(RoutePlanner.Build(badGateway, []).Errors, e => e.Contains("外网网关"));

        var noNetworks = new RouteRequest(Scenario.Wan(), Scenario.Lan(), []);
        Assert.Contains(RoutePlanner.Build(noNetworks, []).Errors, e => e.Contains("目标网段"));

        var noWanGateway = new RouteRequest(Scenario.Wan(gateway: null), Scenario.Lan(), [Ipv4Network.Parse("10.0.0.0/8").Network]);
        Assert.Contains(RoutePlanner.Build(noWanGateway, [Scenario.LanDefault]).Errors, e => e.Contains("外网网卡未检测到默认网关"));
    }

    [Fact]
    public void Warnings_GatewayOutsideSubnet_AndTargetInsideWanSubnet()
    {
        var request = new RouteRequest(
            Scenario.Wan(),
            Scenario.Lan("10.9.9.9"),
            [Ipv4Network.Parse("192.168.1.128/25").Network]);

        var plan = RoutePlanner.Build(request, Scenario.Table().Routes);

        Assert.True(plan.IsValid);
        Assert.Contains(plan.Warnings, w => w.Contains("内网网关 10.9.9.9 不在"));
        Assert.Contains(plan.Warnings, w => w.Contains("192.168.1.128/25 落在外网网卡所在子网"));
    }

    [Fact]
    public void Status_ReflectsActualRouteTable()
    {
        var networks = new[] { Ipv4Network.Parse("10.0.0.0/8").Network, Ipv4Network.Parse("172.16.0.0/12").Network };

        // 配置前：两条默认路由在竞争，内网网段一条都没有
        var before = RouteStatusEvaluator.Evaluate(Scenario.Wan(), Scenario.Lan(), networks, Scenario.Table().Routes);
        Assert.Equal(PathState.Partial, before.Wan);
        Assert.Equal(PathState.Inactive, before.Lan);

        // 只配了一个网段
        var half = new List<RouteEntry>
        {
            Scenario.WanDefault,
            Scenario.Route("10.0.0.0/8", "10.1.1.1", onWan: false, metric: 1),
        };
        var partial = RouteStatusEvaluator.Evaluate(Scenario.Wan(), Scenario.Lan(), networks, half);
        Assert.Equal(PathState.Active, partial.Wan);
        Assert.Equal(PathState.Partial, partial.Lan);
        Assert.True(partial.NetworkActive[networks[0]]);
        Assert.False(partial.NetworkActive[networks[1]]);

        // 全部生效
        half.Add(Scenario.Route("172.16.0.0/12", "10.1.1.1", onWan: false, metric: 1));
        var done = RouteStatusEvaluator.Evaluate(Scenario.Wan(), Scenario.Lan(), networks, half);
        Assert.True(done.FullyActive);

        // 默认路由走了别的网卡
        var wrong = RouteStatusEvaluator.Evaluate(Scenario.Wan(), Scenario.Lan(), networks, [Scenario.LanDefault]);
        Assert.Equal(PathState.Inactive, wrong.Wan);
    }
}
