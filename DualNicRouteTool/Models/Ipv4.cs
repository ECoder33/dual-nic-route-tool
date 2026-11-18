using System.Globalization;
using System.Numerics;

namespace DualNicRouteTool.Models;

/// <summary>IPv4 地址工具。地址统一用主机序的 uint 表示（a.b.c.d 中 a 为最高字节）。</summary>
public static class Ipv4
{
    /// <summary>严格解析点分十进制：必须是 4 段 0-255 的十进制数，不接受前导零、十六进制或缩写形式。</summary>
    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;

        uint result = 0;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3) return false;
            foreach (var ch in part)
            {
                if (ch is < '0' or > '9') return false;
            }
            // 前导零在不同系统里可能被当作八进制，直接拒绝以免歧义
            if (part.Length > 1 && part[0] == '0') return false;

            var n = uint.Parse(part, CultureInfo.InvariantCulture);
            if (n > 255) return false;
            result = (result << 8) | n;
        }

        value = result;
        return true;
    }

    public static string Format(uint value) =>
        $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    /// <summary>前缀长度对应的子网掩码。</summary>
    public static uint Mask(int prefixLength) => prefixLength switch
    {
        <= 0 => 0u,
        >= 32 => uint.MaxValue,
        _ => uint.MaxValue << (32 - prefixLength),
    };

    /// <summary>把子网掩码换算为前缀长度；掩码不连续时返回 false。</summary>
    public static bool TryPrefixLength(uint mask, out int prefixLength)
    {
        prefixLength = BitOperations.PopCount(mask);
        return Mask(prefixLength) == mask;
    }

    /// <summary>解析可用作网关的地址：必须是普通单播地址（排除 0.x、127.x、组播与保留段）。</summary>
    public static bool TryParseGateway(string? text, out uint value)
    {
        if (!TryParse(text, out value)) return false;
        var first = value >> 24;
        return first is >= 1 and <= 223 and not 127;
    }

    /// <summary>规范化地址文本；无法解析时返回 null。</summary>
    public static string? Normalize(string? text) => TryParse(text, out var v) ? Format(v) : null;
}
