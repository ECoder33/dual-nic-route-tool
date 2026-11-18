using System.Windows;
using Microsoft.Win32;

namespace DualNicRouteTool.Services;

/// <summary>
/// 明暗主题。标准控件使用 .NET 自带的 Fluent 主题并跟随系统；
/// 本程序自己的配色（Themes/Palette.*.xaml）在这里随系统设置切换。
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string PaletteMarker = "PaletteName";

    private static bool _initialized;

    public static void Initialize(Application app)
    {
        if (_initialized) return;
        _initialized = true;

        app.ThemeMode = ThemeMode.System;
        ApplyPalette(app);

        // 用户在系统设置里切换明暗模式时同步切换
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                app.Dispatcher.BeginInvoke(() => ApplyPalette(app));
        };
    }

    private static void ApplyPalette(Application app)
    {
        var name = IsDarkMode() ? "Dark" : "Light";
        var dictionaries = app.Resources.MergedDictionaries;

        var index = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (!dictionaries[i].Contains(PaletteMarker)) continue;
            if (Equals(dictionaries[i][PaletteMarker], name)) return;
            index = i;
            break;
        }

        var palette = new ResourceDictionary { Source = new Uri($"/Themes/Palette.{name}.xaml", UriKind.Relative) };
        if (index >= 0) dictionaries[index] = palette;
        else dictionaries.Insert(0, palette);
    }

    private static bool IsDarkMode()
    {
        // 高对比度模式下以系统窗口背景的明暗为准
        if (SystemParameters.HighContrast)
        {
            var c = SystemColors.WindowColor;
            return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
