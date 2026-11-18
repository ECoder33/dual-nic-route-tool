using System.Windows;
using DualNicRouteTool.ViewModels;

namespace DualNicRouteTool.Views;

/// <summary>“一键配置”前的变更预览：列出将新增、删除、保留的路由，确认后才执行。</summary>
public partial class PlanPreviewDialog : Window
{
    public PlanPreviewDialog(PlanPreviewModel model)
    {
        InitializeComponent();
        DataContext = model;

        RemotePanel.Visibility = Show(model.RemoteSession);
        WarningPanel.Visibility = Show(model.Warnings.Count > 0);

        AddPanel.Visibility = Show(model.Adds.Count > 0);
        AddTitle.Text = $"将新增 {model.Adds.Count} 条路由";

        DeletePanel.Visibility = Show(model.Deletes.Count > 0);
        DeleteTitle.Text = $"将删除 {model.Deletes.Count} 条路由";

        KeptPanel.Visibility = Show(model.Kept.Count > 0);
        KeptTitle.Text = $"已符合要求、保持不变 {model.Kept.Count} 条";

        BackupNote.Text = model.MergesPrevious
            ? "上一次配置尚未恢复：本次会沿用最初那份原始路由备份，“恢复原路由”仍然回到最初的状态。"
            : $"执行前会先备份 {model.BackupCount} 条相关的原路由；中途出错会自动撤销本次改动。";
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
