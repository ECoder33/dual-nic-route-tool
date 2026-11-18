using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DualNicRouteTool.Controls;
using DualNicRouteTool.ViewModels;

namespace DualNicRouteTool.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _scrollPending;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        FitToWorkArea();
        ShowAdminState(viewModel.IsAdmin);

        viewModel.OperationCompleted += success => Topology.Flash(success);
        viewModel.Swapped += OnSwapped;
        viewModel.LogAppended += ScrollLogToEnd;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    /// <summary>屏幕较小时把窗口缩到工作区以内，避免底部被任务栏挡住。</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 24));
    }

    private void ShowAdminState(bool isAdmin)
    {
        if (isAdmin) return;

        AdminBar.Visibility = Visibility.Visible;
        AdminBadge.Tag = "Warning";
        AdminBadge.ToolTip = "没有管理员权限，只能查看，不能修改路由表";
        AdminIcon.Data = (Geometry)FindResource("Icon.Shield");
        AdminText.Text = "只读模式";
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            Motion.FadeIn(TabHost, offset: 6, milliseconds: 180);
            if (_viewModel.SelectedTab == 0) ScrollLogToEnd();
        }
    }

    /// <summary>互换内外网：按钮图标转半圈，两张卡片的内容重新淡入。</summary>
    private void OnSwapped()
    {
        var rotate = Motion.GetTransform<RotateTransform>(SwapButton);
        Motion.Animate(rotate, RotateTransform.AngleProperty, 0, 320, from: -180);
        Motion.FadeIn(WanCardContent, offset: 10);
        Motion.FadeIn(LanCardContent, offset: -10);
    }

    /// <summary>新日志出现后滚到底。多条日志连续写入时只滚一次。</summary>
    private void ScrollLogToEnd()
    {
        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    private void OnRestartAsAdmin(object sender, RoutedEventArgs e) => App.RestartAsAdministrator();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            var close = new MessageDialog(
                "操作还在进行",
                "路由正在修改中。现在关闭程序会让配置停在中途——已做的改动会留在路由表里，" +
                "下次启动时可以在“备份历史”里用这次的备份恢复。\n\n建议等待操作完成后再关闭。",
                DialogKind.Warning, "仍然关闭", "继续等待") { Owner = this }.ShowDialog() == true;
            if (!close)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
