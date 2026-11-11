using System.Windows;

namespace TapeRescue.Controls;

/// <summary>Attached properties shared by the app's control templates.</summary>
public static class Ui
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui),
        new FrameworkPropertyMetadata(new CornerRadius(12)));

    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius value) => o.SetValue(CornerRadiusProperty, value);
}
