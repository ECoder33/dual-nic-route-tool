using System.Runtime.InteropServices;

namespace DualNicRouteTool.Native;

/// <summary>
/// Windows IP Helper API（iphlpapi.dll）声明。
/// 直接读写内核路由表，不依赖 ipconfig / netsh / route 的本地化文本输出。
/// </summary>
internal static class IpHelper
{
    public const ushort AF_INET = 2;

    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_FILE_NOT_FOUND = 2;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_NOT_FOUND = 1168;
    public const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;

    /// <summary>MIB_IPPROTO_NETMGMT：手动添加的静态路由（与 route add 一致）。</summary>
    public const int ProtocolNetMgmt = 3;

    /// <summary>SOCKADDR_INET：IPv4 / IPv6 地址的联合体，固定 28 字节。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct SOCKADDR_INET
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(2)] public ushort Port;

        /// <summary>IPv4 地址，网络字节序。</summary>
        [FieldOffset(4)] public uint Ipv4Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IP_ADDRESS_PREFIX
    {
        public SOCKADDR_INET Prefix;
        public byte PrefixLength;
    }

    /// <summary>MIB_IPFORWARD_ROW2，共 104 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IPFORWARD_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IP_ADDRESS_PREFIX DestinationPrefix;
        public SOCKADDR_INET NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public int Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public int Origin;
    }

    /// <summary>MIB_IPFORWARD_TABLE2 中 Table 数组相对表头的偏移（NumEntries 之后按 8 字节对齐）。</summary>
    public const int ForwardTableRowsOffset = 8;

    /// <summary>MIB_IPINTERFACE_ROW 的大小与用到的字段偏移。</summary>
    public const int IpInterfaceRowSize = 168;
    public const int IpInterfaceRowLuidOffset = 8;
    public const int IpInterfaceRowIndexOffset = 16;
    public const int IpInterfaceRowMetricOffset = 148;
    public const int IpInterfaceRowConnectedOffset = 156;

    [DllImport("iphlpapi.dll")]
    public static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    public static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll")]
    public static extern void InitializeIpForwardEntry(ref MIB_IPFORWARD_ROW2 row);

    [DllImport("iphlpapi.dll")]
    public static extern uint CreateIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [DllImport("iphlpapi.dll")]
    public static extern uint DeleteIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [DllImport("iphlpapi.dll")]
    public static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll")]
    public static extern uint GetIpInterfaceEntry(IntPtr row);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int processId);

    public const int AttachParentProcess = -1;
}
