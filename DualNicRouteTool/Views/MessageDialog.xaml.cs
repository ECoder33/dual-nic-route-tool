using System.Windows;
using System.Windows.Media;
using DualNicRouteTool.ViewModels;

namespace DualNicRouteTool.Views;

/// <summary>与主界面同风格的提示 / 确认对话框。</summary>
public partial class MessageDialog : Window
{
    /// <param name="cancelText">传 null 则只显示一个确认按钮（纯提示）。</param>
    public MessageDialog(string title, string message, DialogKind kind, string confirmText = "确定", string? cancelText = null)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            // 没有取消按钮时，Esc 和回车都关闭对话框
            ConfirmButton.IsCancel = true;
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        var (icon, brush) = kind switch
        {
            DialogKind.Question => ("Icon.Help", "AccentBrush"),
            DialogKind.Warning => ("Icon.Warning", "WarningBrush"),
            DialogKind.Error => ("Icon.Error", "ErrorBrush"),
            _ => ("Icon.Info", "AccentBrush"),
        };
        Glyph.Data = (Geometry)FindResource(icon);
        IconTile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, brush);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        // 未以 ShowDialog 打开时不能设置 DialogResult
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
