using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DualNicRouteTool.Controls;

/// <summary>
/// 界面动效的统一入口。所有动画都经过这里，以便遵从系统设置：
/// 用户关闭了“在窗口内显示动画”，或当前处于远程桌面会话时，一律不播放动画，
/// 只把属性直接设为最终值——界面状态完整，只是没有过渡。
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;

    /// <summary>把属性过渡到目标值；动效停用时直接赋值。</summary>
    public static void Animate<T>(T target, DependencyProperty property, double to, double milliseconds, double? from = null)
        where T : DependencyObject, IAnimatable
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = EaseOut };
        if (from is { } start) animation.From = start;
        target.BeginAnimation(property, animation);
    }

    /// <summary>循环动画（旋转、呼吸、流动）；动效停用时不启动。</summary>
    public static void Loop<T>(T target, DependencyProperty property, double from, double to, double milliseconds, bool autoReverse = false)
        where T : DependencyObject, IAnimatable
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            return;
        }

        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = autoReverse,
        });
    }

    public static void Stop<T>(T target, DependencyProperty property) where T : DependencyObject, IAnimatable =>
        target.BeginAnimation(property, null);

    /// <summary>取得元素上指定类型的渲染变换；没有则创建，并以元素中心为原点。</summary>
    public static T GetTransform<T>(UIElement element) where T : Transform, new()
    {
        if (element.RenderTransform is T existing && !existing.IsFrozen) return existing;
        var transform = new T();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transform;
        return transform;
    }

    // ------------------------------------------------------------------
    // Spin：持续旋转
    // ------------------------------------------------------------------

    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSpinChanged));

    public static bool GetSpin(DependencyObject element) => (bool)element.GetValue(SpinProperty);

    public static void SetSpin(DependencyObject element, bool value) => element.SetValue(SpinProperty, value);

    private static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        var rotate = GetTransform<RotateTransform>(element);
        if ((bool)e.NewValue)
        {
            Loop(rotate, RotateTransform.AngleProperty, 0, 360, 900);
        }
        else
        {
            Stop(rotate, RotateTransform.AngleProperty);
            rotate.Angle = 0;
        }
    }

    // ------------------------------------------------------------------
    // Pulse：呼吸闪烁
    // ------------------------------------------------------------------

    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPulseChanged));

    public static bool GetPulse(DependencyObject element) => (bool)element.GetValue(PulseProperty);

    public static void SetPulse(DependencyObject element, bool value) => element.SetValue(PulseProperty, value);

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue) Loop(element, UIElement.OpacityProperty, 1, 0.3, 800, autoReverse: true);
        else Stop(element, UIElement.OpacityProperty);
    }

    // ------------------------------------------------------------------
    // PressScale：按下时轻微缩小
    // ------------------------------------------------------------------

    public static readonly DependencyProperty PressScaleProperty = DependencyProperty.RegisterAttached(
        "PressScale", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPressScaleChanged));

    public static bool GetPressScale(DependencyObject element) => (bool)element.GetValue(PressScaleProperty);

    public static void SetPressScale(DependencyObject element, bool value) => element.SetValue(PressScaleProperty, value);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseLeftButtonDown -= OnPressDown;
        element.PreviewMouseLeftButtonUp -= OnPressUp;
        element.MouseLeave -= OnPressLeave;
        if (!(bool)e.NewValue) return;
        element.PreviewMouseLeftButtonDown += OnPressDown;
        element.PreviewMouseLeftButtonUp += OnPressUp;
        element.MouseLeave += OnPressLeave;
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e) => Scale((UIElement)sender, 0.96);

    private static void OnPressUp(object sender, MouseButtonEventArgs e) => Scale((UIElement)sender, 1);

    private static void OnPressLeave(object sender, MouseEventArgs e) => Scale((UIElement)sender, 1);

    private static void Scale(UIElement element, double to)
    {
        if (!Enabled && to != 1) return;
        var scale = GetTransform<ScaleTransform>(element);
        Animate(scale, ScaleTransform.ScaleXProperty, to, 110);
        Animate(scale, ScaleTransform.ScaleYProperty, to, 110);
    }

    // ------------------------------------------------------------------
    // FadeIn：出现时淡入并轻微上移
    // ------------------------------------------------------------------

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject element) => (bool)element.GetValue(FadeInProperty);

    public static void SetFadeIn(DependencyObject element, bool value) => element.SetValue(FadeInProperty, value);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= OnFadeInLoaded;
        if ((bool)e.NewValue) element.Loaded += OnFadeInLoaded;
    }

    private static void OnFadeInLoaded(object sender, RoutedEventArgs e) => FadeIn((UIElement)sender);

    /// <summary>淡入并从下方 <paramref name="offset"/> 像素处滑到原位。</summary>
    public static void FadeIn(UIElement element, double offset = 8, double milliseconds = 220)
    {
        if (!Enabled) return;
        Animate(element, UIElement.OpacityProperty, 1, milliseconds, from: 0);
        var translate = GetTransform<TranslateTransform>(element);
        Animate(translate, TranslateTransform.YProperty, 0, milliseconds, from: offset);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
