using System.ComponentModel;
using DualNicRouteTool.Models;
using DualNicRouteTool.Native;

namespace DualNicRouteTool.Services;

/// <summary>系统 IPv4 路由表的抽象。真实实现调用 Windows API，测试使用内存实现。</summary>
public interface IRouteTable
{
    /// <summary>
    /// 当前生效的路由，即所在网卡处于已连接状态的路由（与 route print 的范围一致）。
    /// 网卡断开后系统可能仍保留它的路由记录，那些记录不参与选路，这里不返回。
    /// </summary>
    IReadOnlyList<RouteEntry> GetRoutes();

    /// <summary>未连接网卡上残留的路由记录。只用于诊断显示，本工具不读取也不修改它们。</summary>
    IReadOnlyList<RouteEntry> GetInactiveRoutes() => [];

    /// <summary>新增一条非永久路由；失败时抛出 <see cref="RouteOperationException"/>。</summary>
    void Add(RouteEntry route);

    /// <summary>删除一条路由；失败时抛出 <see cref="RouteOperationException"/>。</summary>
    void Delete(RouteEntry route);
}

/// <summary>路由表操作失败，携带 Win32 错误码。</summary>
public sealed class RouteOperationException : Exception
{
    public RouteOperationException(string operation, uint errorCode)
        : base($"{operation}失败：{Describe(errorCode)}（错误码 {errorCode}）")
    {
        ErrorCode = errorCode;
    }

    public uint ErrorCode { get; }

    public bool IsAlreadyExists => ErrorCode == IpHelper.ERROR_OBJECT_ALREADY_EXISTS;

    public bool IsNotFound => ErrorCode is IpHelper.ERROR_NOT_FOUND or IpHelper.ERROR_FILE_NOT_FOUND;

    public bool IsAccessDenied => ErrorCode == IpHelper.ERROR_ACCESS_DENIED;

    private static string Describe(uint errorCode) => errorCode switch
    {
        IpHelper.ERROR_ACCESS_DENIED => "拒绝访问，需要管理员权限",
        IpHelper.ERROR_OBJECT_ALREADY_EXISTS => "路由已存在",
        IpHelper.ERROR_NOT_FOUND or IpHelper.ERROR_FILE_NOT_FOUND => "找不到对应的路由或接口",
        _ => new Win32Exception((int)errorCode).Message,
    };
}
