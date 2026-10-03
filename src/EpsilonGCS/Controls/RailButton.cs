using System.Windows;
using System.Windows.Controls;

namespace EpsilonGCS.Controls;

/// <summary>Left-rail function button. Turns green when <see cref="IsActive"/> is true (as in Epsilon Control).</summary>
public class RailButton : Button
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(RailButton), new PropertyMetadata(false));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }
}
