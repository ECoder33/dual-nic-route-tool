using System.Net.NetworkInformation;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Services;

/// <summary>判断网卡类型。</summary>
public static class NicClassifier
{
    // 蓝牙和虚拟网卡在系统里通常上报为“以太网”，只能靠设备描述里的关键字识别（经验规则）
    private static readonly string[] BluetoothKeywords = ["bluetooth", "蓝牙"];

    private static readonly string[] VpnKeywords =
    [
        "vpn", "wireguard", "wintun", "tap-windows", "tap adapter", "openvpn", "tailscale", "zerotier",
        "anyconnect", "fortinet", "sangfor", "globalprotect", "pangp", "easyconnect", "tunnel",
    ];

    private static readonly string[] VirtualKeywords =
    [
        "hyper-v", "vmware", "virtualbox", "virtual", "vethernet", "docker", "wsl", "loopback", "npcap", "虚拟",
    ];

    public static NicKind Classify(NetworkInterfaceType type, string? description, string? name)
    {
        // 这几类由系统明确上报，判断准确
        switch (type)
        {
            case NetworkInterfaceType.Wireless80211:
                return NicKind.WiFi;
            case NetworkInterfaceType.Wwanpp:
            case NetworkInterfaceType.Wwanpp2:
                return NicKind.Cellular;
            case NetworkInterfaceType.Tunnel:
                return NicKind.Vpn;
            case NetworkInterfaceType.Ppp:
                return NicKind.Dialup;
        }

        var text = $"{description} {name}".ToLowerInvariant();
        if (ContainsAny(text, BluetoothKeywords)) return NicKind.Bluetooth;
        if (ContainsAny(text, VpnKeywords)) return NicKind.Vpn;
        if (ContainsAny(text, VirtualKeywords)) return NicKind.Virtual;

        return type switch
        {
            NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.GigabitEthernet => NicKind.Ethernet,
            _ => NicKind.Unknown,
        };
    }

    private static bool ContainsAny(string text, string[] keywords) => keywords.Any(text.Contains);
}
