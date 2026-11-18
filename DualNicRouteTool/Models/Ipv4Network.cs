using System.Globalization;

namespace DualNicRouteTool.Models;

/// <summary>IPv4 网段（网络地址已按前缀长度清除主机位）。</summary>
public readonly record struct Ipv4Network(uint Address, int PrefixLength)
{
    public uint Mask => Ipv4.Mask(PrefixLength);

    public string AddressText => Ipv4.Format(Address);

    public bool Contains(uint ip) => (ip & Mask) == Address;

    /// <summary>两个网段是否有交集（其中一个包含另一个）。</summary>
    public bool Overlaps(Ipv4Network other)
    {
        var mask = Ipv4.Mask(Math.Min(PrefixLength, other.PrefixLength));
        return (Address & mask) == (other.Address & mask);
    }

    public override string ToString() => $"{AddressText}/{PrefixLength}";

    /// <summary>
    /// 解析一个网段，支持 <c>10.0.0.0/8</c>、<c>10.0.0.0/255.0.0.0</c> 以及不带前缀的单个地址（按 /32 处理）。
    /// 带主机位的写法（如 10.20.1.5/16）会规范化为网络地址，并在结果里标记。
    /// </summary>
    public static NetworkParseResult Parse(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return NetworkParseResult.Fail(text, "网段不能为空");

        var addressPart = text;
        var prefix = 32;

        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            addressPart = text[..slash].Trim();
            var suffix = text[(slash + 1)..].Trim();
            if (suffix.Contains('.'))
            {
                if (!Ipv4.TryParse(suffix, out var mask) || !Ipv4.TryPrefixLength(mask, out prefix))
                    return NetworkParseResult.Fail(text, "子网掩码无效");
            }
            else if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > 32)
            {
                return NetworkParseResult.Fail(text, "前缀长度应为 0-32 的整数");
            }
        }

        if (!Ipv4.TryParse(addressPart, out var address))
            return NetworkParseResult.Fail(text, "IPv4 地址格式无效");

        if (prefix == 0)
            return NetworkParseResult.Fail(text, "不能使用 0.0.0.0/0，默认路由由外网网卡负责");

        var network = address & Ipv4.Mask(prefix);
        var first = network >> 24;
        if (first == 0) return NetworkParseResult.Fail(text, "0.x.x.x 是保留地址");
        if (first == 127) return NetworkParseResult.Fail(text, "127.x.x.x 是本机环回地址");
        if (first >= 224) return NetworkParseResult.Fail(text, "不支持组播或保留地址段");

        return new NetworkParseResult(true, new Ipv4Network(network, prefix), network != address, null, text);
    }

    /// <summary>把用户输入拆成多个网段文本：中英文分号、逗号、空白与换行都可作为分隔符。</summary>
    public static IReadOnlyList<string> SplitList(string? input) =>
        (input ?? string.Empty).Split([';', '；', ',', '，', ' ', '\t', '\r', '\n', '、'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>网段解析结果。</summary>
/// <param name="Normalized">输入带有主机位，已被规范化为网络地址。</param>
public sealed record NetworkParseResult(bool Success, Ipv4Network Network, bool Normalized, string? Error, string Input)
{
    public static NetworkParseResult Fail(string input, string error) => new(false, default, false, error, input);
}
