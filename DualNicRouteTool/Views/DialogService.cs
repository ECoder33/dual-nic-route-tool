using System.Windows;
using DualNicRouteTool.ViewModels;

namespace DualNicRouteTool.Views;

/// <summary>ViewModel 所需对话框能力的实现。</summary>
public sealed class DialogService : IDialogService
{
    private HelpWindow? _help;

    public bool ConfirmPlan(PlanPreviewModel model) =>
        new PlanPreviewDialog(model) { Owner = FindOwner() }.ShowDialog() == true;

    public bool Confirm(string title, string message, string confirmText, DialogKind kind = DialogKind.Question) =>
        new MessageDialog(title, message, kind, confirmText, "取消") { Owner = FindOwner() }.ShowDialog() == true;

    public void Show(string title, string message, DialogKind kind = DialogKind.Info) =>
        new MessageDialog(title, message, kind) { Owner = FindOwner() }.ShowDialog();

    /// <summary>使用说明是非模态的：可以一边看一边操作。重复点击只激活已打开的那一个。</summary>
    public void ShowHelp()
    {
        if (_help is not null)
        {
            if (_help.WindowState == WindowState.Minimized) _help.WindowState = WindowState.Normal;
            _help.Activate();
            return;
        }

        _help = new HelpWindow { Owner = FindOwner() };
        _help.Closed += (_, _) => _help = null;
        _help.Show();
    }

    public void ShowDiagnostics(string report) =>
        new DiagnosticsWindow(report) { Owner = FindOwner(), ShowInTaskbar = false }.Show();

    /// <summary>当前活动窗口；没有则用主窗口。主窗口尚未显示时返回 null（对话框居中于屏幕）。</summary>
    private static Window? FindOwner()
    {
        var app = Application.Current;
        if (app is null) return null;

        foreach (Window window in app.Windows)
        {
            if (window.IsActive && window.IsLoaded) return window;
        }

        return app.MainWindow is { IsLoaded: true } main ? main : null;
    }
}
