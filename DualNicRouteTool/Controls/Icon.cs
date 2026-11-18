using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DualNicRouteTool.Controls;

/// <summary>
/// 矢量图标。图形定义在 Themes/Icons.xaml 中，统一按 24×24 的网格绘制为线条图形，
/// 按控件尺寸等比缩放，颜色跟随所在位置的文字颜色，因此在任意 DPI 和明暗主题下都清晰。
/// </summary>
public sealed class Icon : FrameworkElement
{
    private const double DesignSize = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(Icon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>线条粗细，单位是 24×24 设计网格中的长度。</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>是否用前景色填充图形内部。</summary>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 16 : Math.Min(16, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? 16 : Math.Min(16, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        var data = Data;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (data is null || size <= 0) return;

        var brush = Foreground;
        var pen = new Pen(brush, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        var scale = size / DesignSize;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(Filled ? brush : null, pen, data);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}

/// <summary>给按钮等控件附加图标，供 Themes/Styles.xaml 里的模板读取。</summary>
public static class IconAssist
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached(
        "Data", typeof(Geometry), typeof(IconAssist), new PropertyMetadata(null));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.RegisterAttached(
        "Filled", typeof(bool), typeof(IconAssist), new PropertyMetadata(false));

    /// <summary>为 true 时图标持续旋转（例如刷新进行中）。</summary>
    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(IconAssist), new PropertyMetadata(false));

    public static Geometry? GetData(DependencyObject element) => (Geometry?)element.GetValue(DataProperty);

    public static void SetData(DependencyObject element, Geometry? value) => element.SetValue(DataProperty, value);

    public static bool GetFilled(DependencyObject element) => (bool)element.GetValue(FilledProperty);

    public static void SetFilled(DependencyObject element, bool value) => element.SetValue(FilledProperty, value);

    public static bool GetSpin(DependencyObject element) => (bool)element.GetValue(SpinProperty);

    public static void SetSpin(DependencyObject element, bool value) => element.SetValue(SpinProperty, value);
}
