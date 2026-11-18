using System.Text;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

/// <summary>只读的诊断信息：网卡识别结果、完整 IPv4 路由表、备份列表。用于排查问题，不修改任何东西。</summary>
public static class DiagnosticsReport
{
    public static string Build(IRouteTable table, IBackupStore store)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== {AppEnvironment.AppName} v{AppEnvironment.Version} 诊断信息 =====");
        sb.AppendLine($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"系统        : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"管理员权限  : {(AppEnvironment.IsAdministrator() ? "是" : "否")}");
        sb.AppendLine($"系统启动时间: {AppEnvironment.BootTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // ---------- 网卡 ----------
        IReadOnlyList<NicInfo> nics = [];
        sb.AppendLine("===== 网卡（已连接且拥有 IPv4 地址）=====");
        try
        {
            nics = NetworkInterfaceService.GetInterfaces();
            if (nics.Count == 0) sb.AppendLine("  （未发现任何网卡）");
            foreach (var nic in nics)
            {
                sb.AppendLine($"  [{nic.Index}] {nic.Name}");
                sb.AppendLine($"       类型    : {nic.KindAndSpeed}");
                sb.AppendLine($"       设备    : {nic.Description}");
                sb.AppendLine($"       地址    : {nic.Address}/{nic.PrefixLength}（{(nic.DhcpEnabled ? "DHCP" : "静态")}）");
                sb.AppendLine($"       网关    : {nic.Gateway ?? "无"}");
                sb.AppendLine($"       接口跃点: {nic.InterfaceMetric?.ToString() ?? "未知"}");
                sb.AppendLine($"       LUID    : 0x{nic.Luid:X}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  读取网卡失败：{ex.Message}");
        }

        sb.AppendLine();

        // ---------- 路由表 ----------
        try
        {
            var metrics = nics.Where(n => n.InterfaceMetric is not null).ToDictionary(n => n.Index, n => n.InterfaceMetric!.Value);
            var routes = table.GetRoutes();
            sb.AppendLine($"===== IPv4 路由表（{routes.Count} 条）=====");
            sb.AppendLine("  跃点一栏为“路由跃点 + 接口跃点 = route print 显示值”");
            sb.AppendLine($"  {"目标",-20}{"网关",-18}{"跃点",-16}{"来源",-10}接口");
            foreach (var route in routes)
            {
                var metric = metrics.TryGetValue(route.InterfaceIndex, out var interfaceMetric)
                    ? $"{route.Metric}+{interfaceMetric}={route.Metric + interfaceMetric}"
                    : route.Metric.ToString();
                sb.AppendLine($"  {route.Prefix,-22}{route.NextHopText,-20}{metric,-18}{OriginText(route.Origin),-12}[{route.InterfaceIndex}] {route.InterfaceText}");
            }

            var inactive = table.GetInactiveRoutes().Where(r => !r.IsOnLink).ToList();
            if (inactive.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"===== 未连接网卡上残留的网关路由（{inactive.Count} 条，不生效，route print 不显示，本工具不处理）=====");
                foreach (var route in inactive)
                    sb.AppendLine($"  {route.Prefix,-22}{route.NextHopText,-20}{route.Metric,-18}{OriginText(route.Origin),-12}[{route.InterfaceIndex}] {route.InterfaceText}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"===== IPv4 路由表 =====\n  读取路由表失败：{ex.Message}");
        }

        sb.AppendLine();

        // ---------- 备份 ----------
        sb.AppendLine("===== 备份 =====");
        sb.AppendLine($"  目录: {store.Directory}");
        try
        {
            var boot = AppEnvironment.BootTimeUtc;
            var snapshots = store.LoadAll();
            if (snapshots.Count == 0) sb.AppendLine("  （没有备份）");
            foreach (var s in snapshots)
            {
                sb.AppendLine($"  {s.Id}  状态={s.Status}{(s.IsStale(boot) ? "（开机前创建，已过期）" : string.Empty)}  原始路由 {s.Original.Count} 条，新增 {s.Added.Count} 条");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  读取备份失败：{ex.Message}");
        }

        return sb.ToString();
    }

    public static string OriginText(RouteOrigin origin) => origin switch
    {
        // Windows 把手动添加、DHCP 下发、程序添加的路由都报告为这一类
        RouteOrigin.Manual => "配置",
        RouteOrigin.WellKnown => "系统",
        RouteOrigin.Dhcp => "DHCP",
        RouteOrigin.RouterAdvertisement => "路由通告",
        _ => "其他",
    };
}
