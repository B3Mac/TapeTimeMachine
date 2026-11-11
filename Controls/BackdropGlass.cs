using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TapeRescue.Controls;

/// <summary>
/// A "liquid glass" surface that floats over the video. WPF has no backdrop blur, so this draws the
/// matching part of a low-resolution copy of the picture (<see cref="BackdropSource"/>, stretched over
/// <see cref="BackdropTarget"/>) behind its content. Upscaling the small copy gives the frosted blur;
/// a tint keeps text readable over bright frames, and a top-lit edge gives the glass its rim.
/// </summary>
public sealed class BackdropGlass : Decorator
{
    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.Register(
        nameof(BackdropSource), typeof(ImageSource), typeof(BackdropGlass),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackdropTargetProperty = DependencyProperty.Register(
        nameof(BackdropTarget), typeof(UIElement), typeof(BackdropGlass),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(BackdropGlass),
        new FrameworkPropertyMetadata(999.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(BackdropGlass),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EdgeBrushProperty = DependencyProperty.Register(
        nameof(EdgeBrush), typeof(Brush), typeof(BackdropGlass),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(BackdropGlass),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public ImageSource? BackdropSource
    {
        get => (ImageSource?)GetValue(BackdropSourceProperty);
        set => SetValue(BackdropSourceProperty, value);
    }

    public UIElement? BackdropTarget
    {
        get => (UIElement?)GetValue(BackdropTargetProperty);
        set => SetValue(BackdropTargetProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public Brush? EdgeBrush
    {
        get => (Brush?)GetValue(EdgeBrushProperty);
        set => SetValue(EdgeBrushProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private Rect _lastTargetRect = Rect.Empty;

    public BackdropGlass()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        LayoutUpdated += (_, _) =>
        {
            Rect r = TargetRect();
            if (r != _lastTargetRect)
            {
                _lastTargetRect = r;
                InvalidateVisual();
            }
        };
    }

    protected override Size MeasureOverride(Size constraint)
    {
        Thickness p = Padding;
        var inner = new Size(
            Math.Max(0, constraint.Width - p.Left - p.Right),
            Math.Max(0, constraint.Height - p.Top - p.Bottom));
        Child?.Measure(inner);
        Size desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width + p.Left + p.Right, desired.Height + p.Top + p.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Thickness p = Padding;
        Child?.Arrange(new Rect(p.Left, p.Top,
            Math.Max(0, arrangeSize.Width - p.Left - p.Right),
            Math.Max(0, arrangeSize.Height - p.Top - p.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        double radius = Math.Min(CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2);

        dc.PushClip(new RectangleGeometry(bounds, radius, radius));
        Rect target = TargetRect();
        if (BackdropSource is not null && !target.IsEmpty) dc.DrawImage(BackdropSource, target);
        if (Tint is not null) dc.DrawRectangle(Tint, null, bounds);
        dc.Pop();

        if (EdgeBrush is not null)
        {
            var rim = new Rect(0.5, 0.5, bounds.Width - 1, bounds.Height - 1);
            dc.DrawRoundedRectangle(null, new Pen(EdgeBrush, 1), rim, radius, radius);
        }
    }

    private Rect TargetRect()
    {
        if (BackdropTarget is not FrameworkElement target || !target.IsLoaded || !IsLoaded) return Rect.Empty;
        try
        {
            if (!target.IsAncestorOf(this)) return Rect.Empty;
            return target.TransformToVisual(this).TransformBounds(new Rect(target.RenderSize));
        }
        catch (InvalidOperationException) { return Rect.Empty; }
    }
}
