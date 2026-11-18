using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DualNicRouteTool.Models;
using DualNicRouteTool.Native;

namespace DualNicRouteTool.Services;

/// <summary>通过 IP Helper API 读写系统 IPv4 路由表。</summary>
public sealed class NativeRouteTable : IRouteTable
{
    private readonly object _nameLock = new();
    private Dictionary<uint, string> _interfaceNames = [];
    private DateTime _namesLoadedUtc = DateTime.MinValue;

    public IReadOnlyList<RouteEntry> GetRoutes() => ReadTable(connected: true);

    public IReadOnlyList<RouteEntry> GetInactiveRoutes() => ReadTable(connected: false);

    /// <summary>
    /// 读取路由表，只返回所在网卡连接状态与 <paramref name="connected"/> 相符的路由。
    /// 网卡断开（例如 WiFi 已断开）后，系统仍可能保留它的默认路由记录；
    /// 这些记录不参与选路，route print 也不显示，不能把它们当成“抢默认路由”的冲突去删除。
    /// </summary>
    private List<RouteEntry> ReadTable(bool connected)
    {
        var status = IpHelper.GetIpForwardTable2(IpHelper.AF_INET, out var table);
        if (status != IpHelper.ERROR_SUCCESS)
            throw new RouteOperationException("读取路由表", status);

        try
        {
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<IpHelper.MIB_IPFORWARD_ROW2>();
            var rows = new List<IpHelper.MIB_IPFORWARD_ROW2>(count);
            for (var i = 0; i < count; i++)
            {
                rows.Add(Marshal.PtrToStructure<IpHelper.MIB_IPFORWARD_ROW2>(
                    table + IpHelper.ForwardTableRowsOffset + i * rowSize));
            }

            // 读不到接口信息时按“已连接”处理：宁可多显示，也不漏掉一条可能生效的路由
            var states = rows.Select(r => r.InterfaceIndex).Distinct().ToDictionary(
                index => index,
                index => !TryReadInterface(index, out _, out var isConnected) || isConnected);

            var names = GetInterfaceNames(rows.Select(r => r.InterfaceIndex));
            return rows.Where(row => states[row.InterfaceIndex] == connected).Select(row => new RouteEntry
            {
                Destination = FromNetworkOrder(row.DestinationPrefix.Prefix.Ipv4Address),
                PrefixLength = row.DestinationPrefix.PrefixLength,
                NextHop = FromNetworkOrder(row.NextHop.Ipv4Address),
                InterfaceIndex = row.InterfaceIndex,
                InterfaceLuid = row.InterfaceLuid,
                Metric = row.Metric,
                Origin = Enum.IsDefined(typeof(RouteOrigin), row.Origin) ? (RouteOrigin)row.Origin : RouteOrigin.Manual,
                InterfaceName = names.GetValueOrDefault(row.InterfaceIndex),
            }).ToList();
        }
        finally
        {
            IpHelper.FreeMibTable(table);
        }
    }

    public void Add(RouteEntry route)
    {
        var row = BuildRow(route);
        row.Metric = route.Metric;
        row.Protocol = IpHelper.ProtocolNetMgmt;
        var status = IpHelper.CreateIpForwardEntry2(ref row);
        if (status != IpHelper.ERROR_SUCCESS)
            throw new RouteOperationException($"添加路由 {route.Prefix}", status);
    }

    public void Delete(RouteEntry route)
    {
        var row = BuildRow(route);
        var status = IpHelper.DeleteIpForwardEntry2(ref row);
        if (status != IpHelper.ERROR_SUCCESS)
            throw new RouteOperationException($"删除路由 {route.Prefix}", status);
    }

    /// <summary>把接口索引换算为 LUID；失败返回 0。</summary>
    public static ulong GetInterfaceLuid(uint interfaceIndex) =>
        IpHelper.ConvertInterfaceIndexToLuid(interfaceIndex, out var luid) == IpHelper.ERROR_SUCCESS ? luid : 0;

    /// <summary>读取接口的 IPv4 跃点数；失败返回 null。</summary>
    public static uint? GetInterfaceMetric(uint interfaceIndex) =>
        TryReadInterface(interfaceIndex, out var metric, out _) ? metric : null;

    /// <summary>读取接口的 IPv4 跃点数与连接状态（MIB_IPINTERFACE_ROW）。</summary>
    private static bool TryReadInterface(uint interfaceIndex, out uint metric, out bool connected)
    {
        metric = 0;
        connected = false;

        // 结构体较大且只用到两个字段，按偏移读写；多分配 64 字节作余量
        const int size = IpHelper.IpInterfaceRowSize + 64;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i += 8) Marshal.WriteInt64(buffer, i, 0);
            Marshal.WriteInt16(buffer, 0, (short)IpHelper.AF_INET);
            Marshal.WriteInt32(buffer, IpHelper.IpInterfaceRowIndexOffset, (int)interfaceIndex);
            if (IpHelper.GetIpInterfaceEntry(buffer) != IpHelper.ERROR_SUCCESS) return false;

            metric = (uint)Marshal.ReadInt32(buffer, IpHelper.IpInterfaceRowMetricOffset);
            connected = Marshal.ReadByte(buffer, IpHelper.IpInterfaceRowConnectedOffset) != 0;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IpHelper.MIB_IPFORWARD_ROW2 BuildRow(RouteEntry route)
    {
        if (!Ipv4.TryParse(route.Destination, out var destination) || !Ipv4.TryParse(route.NextHop, out var nextHop))
            throw new ArgumentException($"路由地址无效：{route.Describe()}", nameof(route));

        var row = default(IpHelper.MIB_IPFORWARD_ROW2);
        IpHelper.InitializeIpForwardEntry(ref row);
        row.InterfaceLuid = route.InterfaceLuid;
        row.InterfaceIndex = route.InterfaceIndex;
        row.DestinationPrefix.Prefix.Family = IpHelper.AF_INET;
        row.DestinationPrefix.Prefix.Ipv4Address = BinaryPrimitives.ReverseEndianness(destination);
        row.DestinationPrefix.PrefixLength = (byte)route.PrefixLength;
        row.NextHop.Family = IpHelper.AF_INET;
        row.NextHop.Ipv4Address = BinaryPrimitives.ReverseEndianness(nextHop);
        return row;
    }

    private static string FromNetworkOrder(uint value) => Ipv4.Format(BinaryPrimitives.ReverseEndianness(value));

    /// <summary>接口索引到连接名称的映射，带短时缓存；遇到未知索引时立即刷新。</summary>
    private Dictionary<uint, string> GetInterfaceNames(IEnumerable<uint> indexes)
    {
        lock (_nameLock)
        {
            var expired = DateTime.UtcNow - _namesLoadedUtc > TimeSpan.FromSeconds(15);
            if (!expired && indexes.All(_interfaceNames.ContainsKey)) return _interfaceNames;
            // 缓存未过期但出现了查不到名称的接口：最多每 2 秒重查一次，避免反复枚举
            if (!expired && DateTime.UtcNow - _namesLoadedUtc < TimeSpan.FromSeconds(2)) return _interfaceNames;

            var names = new Dictionary<uint, string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;
                    try
                    {
                        names[(uint)nic.GetIPProperties().GetIPv4Properties().Index] = nic.Name;
                    }
                    catch (NetworkInformationException) { }
                    catch (SocketException) { }
                }
            }
            catch (NetworkInformationException) { }

            _interfaceNames = names;
            _namesLoadedUtc = DateTime.UtcNow;
            return names;
        }
    }
}
