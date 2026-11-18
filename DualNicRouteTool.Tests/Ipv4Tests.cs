using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DualNicRouteTool.Models;
using DualNicRouteTool.Native;
using DualNicRouteTool.Services;

namespace DualNicRouteTool.Tests;

public class Ipv4Tests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8", false)]
    [InlineData("172.16.0.0/12", "172.16.0.0/12", false)]
    [InlineData(" 192.168.50.0/24 ", "192.168.50.0/24", false)]
    [InlineData("10.20.1.5/16", "10.20.0.0/16", true)]
    [InlineData("10.0.0.5", "10.0.0.5/32", false)]
    [InlineData("10.0.0.0/255.0.0.0", "10.0.0.0/8", false)]
    [InlineData("192.168.1.77/255.255.255.0", "192.168.1.0/24", true)]
    public void Parse_AcceptsValidNetworks(string input, string expected, bool normalized)
    {
        var result = Ipv4Network.Parse(input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Network.ToString());
        Assert.Equal(normalized, result.Normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10.0.0/8")]
    [InlineData("10.0.0.0.0/8")]
    [InlineData("256.0.0.0/8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/255.0.255.0")]
    [InlineData("010.0.0.0/8")]
    [InlineData("0x0a.0.0.0/8")]
    [InlineData("10")]
    [InlineData("0.0.0.0/0")]
    [InlineData("127.0.0.0/8")]
    [InlineData("224.0.0.0/4")]
    public void Parse_RejectsInvalidNetworks(string input)
    {
        var result = Ipv4Network.Parse(input);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void SplitList_AcceptsChineseAndEnglishSeparators()
    {
        var parts = Ipv4Network.SplitList("10.0.0.0/8;172.16.0.0/12；192.168.0.0/16, 10.9.0.0/16\n10.8.0.0/16");

        Assert.Equal(new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "10.9.0.0/16", "10.8.0.0/16" }, parts.ToArray());
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("10.0.0.254", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("fe80::1", false)]
    public void TryParseGateway_OnlyAcceptsUnicast(string input, bool expected)
    {
        Assert.Equal(expected, Ipv4.TryParseGateway(input, out _));
    }

    [Fact]
    public void Network_ContainsAndOverlaps()
    {
        var wide = Ipv4Network.Parse("192.168.0.0/16").Network;
        var narrow = Ipv4Network.Parse("192.168.1.0/24").Network;
        var other = Ipv4Network.Parse("10.0.0.0/8").Network;
        Ipv4.TryParse("192.168.1.1", out var host);

        Assert.True(wide.Contains(host));
        Assert.True(narrow.Contains(host));
        Assert.False(other.Contains(host));
        Assert.True(wide.Overlaps(narrow));
        Assert.True(narrow.Overlaps(wide));
        Assert.False(wide.Overlaps(other));
    }

    /// <summary>P/Invoke 结构体必须与 Windows SDK 的内存布局一致，否则读出的路由全是错的。</summary>
    [Fact]
    public void NativeStructs_MatchWindowsLayout()
    {
        Assert.Equal(28, Marshal.SizeOf<IpHelper.SOCKADDR_INET>());
        Assert.Equal(32, Marshal.SizeOf<IpHelper.IP_ADDRESS_PREFIX>());
        Assert.Equal(104, Marshal.SizeOf<IpHelper.MIB_IPFORWARD_ROW2>());
        Assert.Equal(12, (int)Marshal.OffsetOf<IpHelper.MIB_IPFORWARD_ROW2>(nameof(IpHelper.MIB_IPFORWARD_ROW2.DestinationPrefix)));
        Assert.Equal(44, (int)Marshal.OffsetOf<IpHelper.MIB_IPFORWARD_ROW2>(nameof(IpHelper.MIB_IPFORWARD_ROW2.NextHop)));
        Assert.Equal(84, (int)Marshal.OffsetOf<IpHelper.MIB_IPFORWARD_ROW2>(nameof(IpHelper.MIB_IPFORWARD_ROW2.Metric)));
        Assert.Equal(100, (int)Marshal.OffsetOf<IpHelper.MIB_IPFORWARD_ROW2>(nameof(IpHelper.MIB_IPFORWARD_ROW2.Origin)));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6 AX201 160MHz", "WLAN", NicKind.WiFi)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Microsoft Wi-Fi Direct Virtual Adapter", "本地连接* 1", NicKind.WiFi)]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek PCIe GbE Family Controller", "以太网", NicKind.Ethernet)]
    [InlineData(NetworkInterfaceType.GigabitEthernet, "Intel(R) Ethernet Connection I219-V", "以太网 2", NicKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Ethernet, "Bluetooth Device (Personal Area Network)", "蓝牙网络连接", NicKind.Bluetooth)]
    [InlineData(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)", NicKind.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "VMware Virtual Ethernet Adapter for VMnet8", "VMware Network Adapter VMnet8", NicKind.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "TAP-Windows Adapter V9", "本地连接", NicKind.Vpn)]
    [InlineData(NetworkInterfaceType.Tunnel, "WireGuard Tunnel", "wg0", NicKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ppp, "WAN Miniport (PPPOE)", "宽带连接", NicKind.Dialup)]
    [InlineData(NetworkInterfaceType.Wwanpp, "Generic Mobile Broadband Adapter", "手机网络", NicKind.Cellular)]
    [InlineData(NetworkInterfaceType.TokenRing, "Some Legacy Adapter", "本地连接 9", NicKind.Unknown)]
    public void NicClassifier_RecognisesAdapterKinds(NetworkInterfaceType type, string description, string name, NicKind expected)
    {
        Assert.Equal(expected, NicClassifier.Classify(type, description, name));
    }
}
