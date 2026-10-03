using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EpsilonGCS.Flyouts;

public sealed class NumField
{
    public TextBox Box { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }

    public double Value
    {
        get => double.TryParse(Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, Min, Max) : Min;
        set => Box.Text = value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public int Int => (int)Math.Round(Value);
}

public sealed class ChoiceField
{
    public ComboBox Box { get; init; }
    public int[] Values { get; init; }

    public int Value
    {
        get => Box.SelectedIndex >= 0 ? Values[Box.SelectedIndex] : Values[0];
        set
        {
            int i = Array.IndexOf(Values, value);
            Box.SelectedIndex = i >= 0 ? i : 0;
        }
    }
}

public sealed class BoolField
{
    public CheckBox Box { get; init; }

    public bool Value
    {
        get => Box.IsChecked == true;
        set => Box.IsChecked = value;
    }
}

public sealed class TextField
{
    public TextBox Box { get; init; }

    public string Value
    {
        get => Box.Text?.Trim() ?? "";
        set => Box.Text = value ?? "";
    }
}

/// <summary>Builds simple label/value forms in code, keeping each flyout page short and readable.</summary>
public sealed class FormBuilder
{
    private readonly Panel _root;
    public double LabelWidth { get; set; } = 150;

    public FormBuilder(Panel root) => _root = root;

    public void Section(string title) =>
        _root.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("SectionHeader") });

    public TextBlock Note(string text)
    {
        var tb = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            FontSize = 11, Margin = new Thickness(0, 2, 0, 4),
        };
        _root.Children.Add(tb);
        return tb;
    }

    public void Row(string label, FrameworkElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        g.Children.Add(tb);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        _root.Children.Add(g);
    }

    public NumField Number(string label, double value, double min, double max, string tooltip = null)
    {
        var box = new TextBox { Height = 22, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = tooltip ?? $"{min} .. {max}" };
        var f = new NumField { Box = box, Min = min, Max = max };
        f.Value = value;
        Row(label, box);
        return f;
    }

    public TextField Text(string label, string value, string tooltip = null)
    {
        var box = new TextBox { Height = 22, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = tooltip };
        var f = new TextField { Box = box };
        f.Value = value;
        Row(label, box);
        return f;
    }

    public ChoiceField Choice(string label, int selected, params (int Value, string Text)[] options)
    {
        var box = new ComboBox { Height = 22 };
        foreach (var o in options) box.Items.Add(o.Text);
        var f = new ChoiceField { Box = box, Values = options.Select(o => o.Value).ToArray() };
        f.Value = selected;
        Row(label, box);
        return f;
    }

    /// <summary>Choice whose values are 0..n-1 in order.</summary>
    public ChoiceField Choice(string label, int selected, params string[] texts) =>
        Choice(label, selected, texts.Select((t, i) => (i, t)).ToArray());

    public BoolField Check(string label, bool value)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
        _root.Children.Add(cb);
        return new BoolField { Box = cb };
    }

    public WrapPanel Buttons(params (string Text, Action Click)[] buttons)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
        foreach (var (text, click) in buttons)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("PanelButton") };
            b.Click += (_, _) => click();
            wp.Children.Add(b);
        }
        _root.Children.Add(wp);
        return wp;
    }

    public TextBlock Value(string label)
    {
        var tb = new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        Row(label, tb);
        return tb;
    }

    public void Add(UIElement element) => _root.Children.Add(element);

    public static (int, string)[] Range(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(i => (i, i.ToString())).ToArray();
}
