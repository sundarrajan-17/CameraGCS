using System.Windows;
using System.Windows.Controls;
using Epsilon.Core.Protocol;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

/// <summary>
/// Base for the pages opened from the vertical side tabs (Sensors, Telemetry, Network, ...).
/// Pages are built in code with <see cref="FormBuilder"/>. "Read" buttons send a zero-length
/// request; the gimbal answers in the same format as the Set message and <see cref="OnSetting"/>
/// fills the fields.
/// </summary>
public abstract class FlyoutPage : UserControl
{
    protected StackPanel Root { get; }
    protected FormBuilder F { get; }

    public abstract string Title { get; }

    protected FlyoutPage()
    {
        Root = new StackPanel { Margin = new Thickness(10, 0, 14, 12) };
        F = new FormBuilder(Root);
        Content = new ScrollViewer
        {
            Content = Root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    /// <summary>Called each time the page is shown.</summary>
    public virtual void OnOpened() { }

    /// <summary>Called on the UI thread for every non-empty packet from the gimbal while the page is open.</summary>
    public virtual void OnSetting(Packet p) { }

    /// <summary>Called on the UI thread for every EPSILON_GLOBAL_STATUS while the page is open.</summary>
    public virtual void OnStatus(GlobalStatus s) { }

    protected static void Send(Packet p) => Gcs.Send(p);
    protected static void Req(MessageId id) => Gcs.Send(Cmd.Request(id));

    protected static bool Confirm(string text) =>
        MessageBox.Show(text, "Epsilon GCS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
