using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace DualNicRouteTool.Views;

/// <summary>只读诊断报告（与命令行 --debug 输出的内容相同）。</summary>
public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(string report)
    {
        InitializeComponent();
        ReportText.Text = report;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportText.Text);
            StatusText.Text = "已复制到剪贴板";
        }
        catch (COMException)
        {
            StatusText.Text = "复制失败：剪贴板正被其他程序占用";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存诊断信息",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"route_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, ReportText.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = $"已保存到 {dialog.FileName}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
