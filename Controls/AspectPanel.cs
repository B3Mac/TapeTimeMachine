using System.Windows;
using System.Windows.Controls;

namespace TapeRescue.Controls;

/// <summary>
/// Sizes its child as large as possible while an inner "screen" keeps <see cref="AspectRatio"/>
/// (4:3 for Hi8). The child is the screen plus <see cref="HorizontalInset"/> on each side and
/// <see cref="ExtraHeight"/> of fixed-height chrome (frame bars, captions). Centred both ways.
/// </summary>
public sealed class AspectPanel : Panel
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(AspectPanel),
        new FrameworkPropertyMetadata(4.0 / 3.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HorizontalInsetProperty = DependencyProperty.Register(
        nameof(HorizontalInset), typeof(double), typeof(AspectPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ExtraHeightProperty = DependencyProperty.Register(
        nameof(ExtraHeight), typeof(double), typeof(AspectPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxScreenWidthProperty = DependencyProperty.Register(
        nameof(MaxScreenWidth), typeof(double), typeof(AspectPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double AspectRatio { get => (double)GetValue(AspectRatioProperty); set => SetValue(AspectRatioProperty, value); }
    public double HorizontalInset { get => (double)GetValue(HorizontalInsetProperty); set => SetValue(HorizontalInsetProperty, value); }
    public double ExtraHeight { get => (double)GetValue(ExtraHeightProperty); set => SetValue(ExtraHeightProperty, value); }
    public double MaxScreenWidth { get => (double)GetValue(MaxScreenWidthProperty); set => SetValue(MaxScreenWidthProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        Size size = Fit(available);
        foreach (UIElement child in InternalChildren) child.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Size size = Fit(final);
        double x = Math.Max(0, (final.Width - size.Width) / 2);
        double y = Math.Max(0, (final.Height - size.Height) / 2);
        foreach (UIElement child in InternalChildren) child.Arrange(new Rect(x, y, size.Width, size.Height));
        return final;
    }

    private Size Fit(Size available)
    {
        double ratio = AspectRatio > 0 ? AspectRatio : 4.0 / 3.0;
        double inset = HorizontalInset * 2, extra = ExtraHeight;
        double w = available.Width - inset, h = available.Height - extra;
        if (double.IsInfinity(w) && double.IsInfinity(h)) { w = 640; h = 480; }
        else if (double.IsInfinity(w)) w = h * ratio;
        else if (double.IsInfinity(h)) h = w / ratio;

        double screenW = Math.Max(0, Math.Min(Math.Min(w, h * ratio), MaxScreenWidth));
        return new Size(screenW + inset, screenW / ratio + extra);
    }
}
