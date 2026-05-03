using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MarkMello.Presentation.Views;

public partial class AppAboutPanelView : UserControl
{
    public AppAboutPanelView()
    {
        InitializeComponent();
    }

    private async void OnAboutLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string rawUrl })
        {
            return;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
        {
            return;
        }

        await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
    }
}
