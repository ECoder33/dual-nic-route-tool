using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

/// <summary>枚举本机网卡。</summary>
public static class NetworkInterfaceService
{
    /// <summary>返回已连接且拥有 IPv4 地址的网卡（不含环回），有网关的排在前面。</summary>
    public static IReadOnlyList<NicInfo> GetInterfaces()
    {
        var result = new List<NicInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;

            try
            {
                var properties = nic.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();

                var addresses = properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .ToList();
                if (addresses.Count == 0) continue;

                // 有多个地址时优先取非 169.254.x.x（自动专用地址）的那个
                var address = addresses.FirstOrDefault(a => !IsLinkLocal(a.Address)) ?? addresses[0];

                var gateway = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

                var index = (uint)ipv4.Index;
                result.Add(new NicInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    Kind = NicClassifier.Classify(nic.NetworkInterfaceType, nic.Description, nic.Name),
                    Index = index,
                    Luid = NativeRouteTable.GetInterfaceLuid(index),
                    Address = address.Address.ToString(),
                    PrefixLength = address.PrefixLength,
                    Gateway = gateway?.ToString(),
                    SpeedBps = nic.Speed,
                    DhcpEnabled = SafeIsDhcpEnabled(ipv4),
                    InterfaceMetric = NativeRouteTable.GetInterfaceMetric(index),
                });
            }
            catch (NetworkInformationException)
            {
                // 个别网卡在枚举瞬间断开，属性读取会失败，跳过即可
            }
        }

        return result
            .OrderByDescending(n => n.Gateway is not null)
            .ThenBy(n => n.Kind is NicKind.Virtual or NicKind.Vpn or NicKind.Bluetooth)
            .ThenBy(n => n.Index)
            .ToList();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool SafeIsDhcpEnabled(IPv4InterfaceProperties properties)
    {
        try
        {
            return properties.IsDhcpEnabled;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
