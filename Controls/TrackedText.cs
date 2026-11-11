using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;

namespace TapeRescue.Controls;

/// <summary>
/// A single line of text with letter-spacing (WPF's TextBlock has none). Used for the design's
/// spaced capital labels such as "CONNECTED CAMCORDER". Tracking is in em, like CSS letter-spacing.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => AutomationProperties.SetName(d, (string)e.NewValue)));

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(0.15, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private FormattedText Glyph(string s, Brush brush) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        string text = Text ?? "";
        if (text.Length == 0) return new Size(0, Glyph("X", Foreground).Height);
        double width = 0, height = 0, gap = Tracking * FontSize;
        foreach (char c in text)
        {
            var g = Glyph(c.ToString(), Foreground);
            width += g.WidthIncludingTrailingWhitespace + gap;
            height = Math.Max(height, g.Height);
        }
        return new Size(Math.Max(0, width - gap), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double x = 0, gap = Tracking * FontSize;
        foreach (char c in Text ?? "")
        {
            var g = Glyph(c.ToString(), Foreground);
            dc.DrawText(g, new Point(x, 0));
            x += g.WidthIncludingTrailingWhitespace + gap;
        }
    }
}
