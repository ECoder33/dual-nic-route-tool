using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DualNicRouteTool.Models;

namespace DualNicRouteTool.Controls;

public partial class RouteTopologyView : UserControl
{
    /// <summary>左侧两张网卡卡片之间的间距；用于让线路起点与卡片中心对齐。</summary>
    public const double CardGap = 14;

    private const double StartX = 16;
    private readonly DispatcherTimer _badgeTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };

    public RouteTopologyView()
    {
        InitializeComponent();
        _badgeTimer.Tick += (_, _) => HideBadge();
        Loaded += (_, _) =>
        {
            LayoutStage();
            ApplyStates();
        };
        SizeChanged += (_, _) => LayoutStage();
        IsVisibleChanged += (_, _) => ApplyStates();
    }

    // ------------------------------------------------------------------
    // 依赖属性
    // ------------------------------------------------------------------

    public static readonly DependencyProperty WanStateProperty = Register(nameof(WanState), PathState.Inactive, OnStateChanged);
    public static readonly DependencyProperty LanStateProperty = Register(nameof(LanState), PathState.Inactive, OnStateChanged);
    public static readonly DependencyProperty WanDetailProperty = Register(nameof(WanDetail), string.Empty);
    public static readonly DependencyProperty LanDetailProperty = Register(nameof(LanDetail), string.Empty);
    public static readonly DependencyProperty WanCaptionTextProperty = Register(nameof(WanCaptionText), string.Empty);
    public static readonly DependencyProperty LanCaptionTextProperty = Register(nameof(LanCaptionText), string.Empty);
    public static readonly DependencyProperty IsBusyProperty = Register(nameof(IsBusy), false, OnBusyChanged);

    public PathState WanState
    {
        get => (PathState)GetValue(WanStateProperty);
        set => SetValue(WanStateProperty, value);
    }

    public PathState LanState
    {
        get => (PathState)GetValue(LanStateProperty);
        set => SetValue(LanStateProperty, value);
    }

    /// <summary>“互联网”节点下方的说明。</summary>
    public string WanDetail
    {
        get => (string)GetValue(WanDetailProperty);
        set => SetValue(WanDetailProperty, value);
    }

    /// <summary>“内网网段”节点下方的说明。</summary>
    public string LanDetail
    {
        get => (string)GetValue(LanDetailProperty);
        set => SetValue(LanDetailProperty, value);
    }

    /// <summary>外网线路起点旁的文字，如“WLAN · 网关 192.168.1.1”。</summary>
    public string WanCaptionText
    {
        get => (string)GetValue(WanCaptionTextProperty);
        set => SetValue(WanCaptionTextProperty, value);
    }

    public string LanCaptionText
    {
        get => (string)GetValue(LanCaptionTextProperty);
        set => SetValue(LanCaptionTextProperty, value);
    }

    /// <summary>正在配置或恢复：盾牌脉冲。</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    private static DependencyProperty Register<T>(string name, T defaultValue, PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(RouteTopologyView), new PropertyMetadata(defaultValue, callback));

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RouteTopologyView)d).ApplyStates();

    private static void OnBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (RouteTopologyView)d;
        if ((bool)e.NewValue)
        {
            view.HideBadge();
            Motion.Loop(view.ShieldScale, ScaleTransform.ScaleXProperty, 1, 1.07, 650, autoReverse: true);
            Motion.Loop(view.ShieldScale, ScaleTransform.ScaleYProperty, 1, 1.07, 650, autoReverse: true);
        }
        else
        {
            Motion.Stop(view.ShieldScale, ScaleTransform.ScaleXProperty);
            Motion.Stop(view.ShieldScale, ScaleTransform.ScaleYProperty);
        }
    }

    // ------------------------------------------------------------------
    // 操作结果提示
    // ------------------------------------------------------------------

    /// <summary>在盾牌上弹出结果徽标：成功为对勾；失败为叉并左右抖动。几秒后自动消失。</summary>
    public void Flash(bool success)
    {
        BadgeBackground.SetResourceReference(Shape.FillProperty, success ? "SuccessBrush" : "ErrorBrush");
        BadgeIcon.Data = (Geometry)FindResource(success ? "Icon.Check" : "Icon.Close");
        Badge.Visibility = Visibility.Visible;
        Motion.Animate(Badge, OpacityProperty, 1, 1);
        Motion.Animate(BadgeScale, ScaleTransform.ScaleXProperty, 1, 260, from: 0.2);
        Motion.Animate(BadgeScale, ScaleTransform.ScaleYProperty, 1, 260, from: 0.2);

        if (!success && Motion.Enabled)
        {
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            foreach (var (offset, percent) in new[] { (-7d, 0.15), (7d, 0.35), (-5d, 0.55), (4d, 0.75), (0d, 1.0) })
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(offset, KeyTime.FromPercent(percent)));
            ShieldShift.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    private void HideBadge()
    {
        _badgeTimer.Stop();
        if (Badge.Visibility != Visibility.Visible) return;
        if (Motion.Enabled) Motion.Animate(Badge, OpacityProperty, 0, 300);
        else Badge.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // 线路状态
    // ------------------------------------------------------------------

    private void ApplyStates()
    {
        ApplyLane(WanState, "WanBrush", [WanInBase, WanOutBase], [WanInFlow, WanOutFlow], WanArrow, WanDot, WanNode);
        ApplyLane(LanState, "LanBrush", [LanInBase, LanOutBase], [LanInFlow, LanOutFlow], LanArrow, LanDot, LanNode);
    }

    private void ApplyLane(PathState state, string colorKey, Path[] bases, Path[] flows, Shape arrow, Shape dot, UIElement node)
    {
        var brushKey = state switch
        {
            PathState.Active => colorKey,
            PathState.Partial => "WarningBrush",
            _ => "NeutralLineBrush",
        };

        foreach (var path in bases)
        {
            path.SetResourceReference(Shape.StrokeProperty, brushKey);
            // 已生效：淡色实线作底，上面叠一层流动的圆点；其余状态：静态虚线
            path.Opacity = state == PathState.Active ? 0.28 : 1;
            path.StrokeDashArray = state switch
            {
                PathState.Active => null,
                PathState.Partial => [2.4, 2.2],
                _ => [0.6, 2.4],
            };
        }

        var flowing = state == PathState.Active && IsVisible;
        foreach (var path in flows)
        {
            path.Visibility = state == PathState.Active ? Visibility.Visible : Visibility.Collapsed;
            // 虚线周期为 3（单位：线宽），偏移从 0 减到 -3 正好前进一个周期，循环起来无缝
            if (flowing) Motion.Loop(path, Shape.StrokeDashOffsetProperty, 0, -3, 620);
            else Motion.Stop(path, Shape.StrokeDashOffsetProperty);
        }

        arrow.SetResourceReference(Shape.FillProperty, brushKey);
        dot.SetResourceReference(Shape.FillProperty, brushKey);
        node.Opacity = state == PathState.Inactive ? 0.5 : 1;
    }

    // ------------------------------------------------------------------
    // 布局
    // ------------------------------------------------------------------

    private void LayoutStage()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 100 || height < 100) return;

        Stage.Width = width;
        Stage.Height = height;

        // 两条线路的高度对准左侧两张卡片的中心
        var wanY = (height - CardGap) / 4;
        var lanY = height - wanY;
        var centerY = height / 2;

        // 右侧目的地节点
        var nodeWidth = Math.Clamp(width * 0.36, 150, 230);
        var nodeX = width - nodeWidth - 6;
        WanNode.Width = LanNode.Width = nodeWidth;
        Place(WanNode, nodeX, wanY - WanNode.Height / 2);
        Place(LanNode, nodeX, lanY - LanNode.Height / 2);

        // 中间的盾牌
        var arrowX = nodeX - 18;
        var shieldSize = Math.Clamp(height * 0.3, 70, 104);
        var centerX = (StartX + arrowX) / 2;
        Shield.Width = Shield.Height = shieldSize;
        Place(Shield, centerX - shieldSize / 2, centerY - shieldSize / 2);

        // 线路：起点 → 盾牌左侧，盾牌右侧 → 箭头（线路端点藏在盾牌下面）
        var inX = centerX - shieldSize * 0.26;
        var outX = centerX + shieldSize * 0.26;
        var spread = shieldSize * 0.15;
        WanInBase.Data = WanInFlow.Data = Curve(new Point(StartX, wanY), new Point(inX, centerY - spread));
        LanInBase.Data = LanInFlow.Data = Curve(new Point(StartX, lanY), new Point(inX, centerY + spread));
        WanOutBase.Data = WanOutFlow.Data = Curve(new Point(outX, centerY - spread), new Point(arrowX, wanY));
        LanOutBase.Data = LanOutFlow.Data = Curve(new Point(outX, centerY + spread), new Point(arrowX, lanY));

        Place(WanDot, StartX - WanDot.Width / 2, wanY - WanDot.Height / 2);
        Place(LanDot, StartX - LanDot.Width / 2, lanY - LanDot.Height / 2);
        Place(WanArrow, arrowX, wanY);
        Place(LanArrow, arrowX, lanY);

        // 说明文字：外网在线路上方，内网在线路下方
        var captionWidth = Math.Max(80, inX - StartX);
        WanCaption.MaxWidth = LanCaption.MaxWidth = captionWidth;
        Place(WanCaption, StartX - 4, wanY - 36);
        Place(LanCaption, StartX - 4, lanY + 14);
    }

    private static void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    /// <summary>两端水平出入的 S 形曲线。</summary>
    private static Geometry Curve(Point from, Point to)
    {
        var dx = to.X - from.X;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(from, isFilled: false, isClosed: false);
            context.BezierTo(new Point(from.X + dx * 0.55, from.Y), new Point(to.X - dx * 0.5, to.Y), to, isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }
}
