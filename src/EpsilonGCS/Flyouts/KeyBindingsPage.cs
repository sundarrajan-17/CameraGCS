using System.Windows;
using System.Windows.Controls;

namespace EpsilonGCS.Flyouts;

public sealed class KeyBindingsPage : FlyoutPage
{
    public override string Title => "Key Bindings";

    public static readonly (string Key, string Action)[] Bindings =
    {
        ("← → ↑ ↓", "Pan / tilt (track-box nudge when NUDGE is on)"),
        ("Z / X", "Zoom out / zoom in"),
        ("F / G", "Focus far / near (manual focus)"),
        ("Space", "Start tracking at the cross (click-to-track mode)"),
        ("Esc", "RATE mode (stop tracking)"),
        ("1 .. 7", "RATE, AID, SCN, VHCL, GEO, PILOT, STOW"),
        ("I", "Toggle EO / IR"),
        ("S", "Snapshot"),
        ("R", "Start / stop recording"),
        ("N", "NUC / FFC"),
        ("C", "Center gimbal (pilot view direction)"),
        ("Ctrl + mouse wheel", "Zoom (over the video)"),
        ("F11 / double-click video", "Fullscreen video on / off"),
        ("F5", "Re-synchronise live video (restart the player, keeps RTSP restream running)"),
        ("Left click on video", "Track there (when Track is on)"),
        ("Right click on map", "Geo lock, copy coordinates, home, clear track / POIs"),
        ("Gamepad left stick", "Pan / tilt (J.STICK on)"),
        ("Gamepad triggers / LB RB", "Zoom out-in / focus"),
        ("Gamepad A / B / X / Y", "Track at cross / RATE / snapshot / EO-IR"),
    };

    public KeyBindingsPage()
    {
        F.Section("Keyboard and gamepad");
        F.Note("Keys work when the video or map has focus (not while typing in a text box).");
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < Bindings.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = Bindings[i].Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 6, 3) };
            var a = new TextBlock { Text = Bindings[i].Action, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(k, i);
            Grid.SetRow(a, i);
            Grid.SetColumn(a, 1);
            grid.Children.Add(k);
            grid.Children.Add(a);
        }
        F.Add(grid);
    }
}
