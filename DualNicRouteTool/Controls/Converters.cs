using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Controls;

/// <summary>网卡类型 → 图标图形（Themes/Icons.xaml 中键名为 Icon.&lt;类型&gt; 的资源）。</summary>
public sealed class NicKindToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current?.TryFindResource($"Icon.{value as NicKind? ?? NicKind.Unknown}");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// 通用的可见性转换：bool 为 true、字符串非空、对象非 null、数字非 0 时显示。
/// 参数传 "invert" 则取反。
/// </summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            _ => true,
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>整数是否等于参数。用于把一组 RadioButton 绑定到同一个“当前页签”属性。</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = value is int i && int.TryParse(parameter as string, out var p) && i == p;
        if (targetType == typeof(Visibility)) return equal ? Visibility.Visible : Visibility.Collapsed;
        return equal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter as string, out var p) ? p : Binding.DoNothing;
}
