using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MarkMello.Presentation.Views;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Fork-only About panel that reuses the upstream <see cref="AppAboutPanelView"/>
/// content and appends an "Applicate additions by ..." credit row beneath the
/// existing upstream credits, so both copyright notices stay visible to the
/// user. The fork-overlay rule forbids editing upstream files, so this view
/// patches the visual tree on attach instead of duplicating the layout.
/// </summary>
internal sealed class ApplicateAppAboutPanelView : AppAboutPanelView
{
    private const string ForkAuthorDisplayName = "Dmitry Denisenko (applicate2628)";
    private const string ForkAuthorUrl = "https://github.com/applicate2628";
    private const string ForkCreditsPrefix = "Applicate additions by ";
    private const string ForkCreditsPeriod = ".";

    private bool _forkRowInjected;

    public ApplicateAppAboutPanelView()
    {
        // Inject as soon as the visual tree is materialized. The popup
        // first attaches OnAttachedToVisualTree but the inner XAML
        // descendants may not all be reachable via GetVisualDescendants
        // on that first tick; on the next dispatcher tick they are.
        // Loaded fires after layout has measured and arranged the tree,
        // guaranteeing the upstream WrapPanel is present. We hook BOTH
        // events: Loaded as the reliable path, OnAttachedToVisualTree
        // as a fallback in case Loaded fires earlier in some host.
        Loaded += (_, _) => InjectForkCreditsRow();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InjectForkCreditsRow();
    }

    private void InjectForkCreditsRow()
    {
        if (_forkRowInjected)
        {
            return;
        }

        var upstreamWrapPanel = this.GetVisualDescendants()
            .OfType<WrapPanel>()
            .FirstOrDefault(IsUpstreamCreditsWrapPanel);
        if (upstreamWrapPanel?.Parent is not StackPanel creditsStack)
        {
            return;
        }

        var forkRow = BuildForkCreditsWrapPanel();
        var insertIndex = creditsStack.Children.IndexOf(upstreamWrapPanel) + 1;
        creditsStack.Children.Insert(insertIndex, forkRow);
        _forkRowInjected = true;
    }

    private static bool IsUpstreamCreditsWrapPanel(WrapPanel panel)
    {
        return panel.Children.OfType<Button>()
            .Any(b => b.Classes.Contains("mm-inline-link"));
    }

    private WrapPanel BuildForkCreditsWrapPanel()
    {
        var prefix = new TextBlock
        {
            Classes = { "mm-setting-body" },
            Text = ForkCreditsPrefix
        };

        var link = new Button
        {
            Classes = { "mm-inline-link" },
            Tag = ForkAuthorUrl,
            Content = ForkAuthorDisplayName
        };
        link.Click += OnForkLinkClickAsync;

        var period = new TextBlock
        {
            Classes = { "mm-setting-body" },
            Text = ForkCreditsPeriod
        };

        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemHeight = 22
        };
        wrap.Children.Add(prefix);
        wrap.Children.Add(link);
        wrap.Children.Add(period);
        return wrap;
    }

    private async void OnForkLinkClickAsync(object? sender, RoutedEventArgs e)
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

        try
        {
            await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Launcher failed (no default browser, sandbox, etc). The link
            // does not produce side effects on failure and there is no
            // user-visible error surface for this case in the panel.
        }
    }
}
