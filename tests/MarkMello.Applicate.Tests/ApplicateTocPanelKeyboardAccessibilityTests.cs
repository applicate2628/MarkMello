using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Applicate.Tests.Fakes;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Behavioural guards for work-items/bugs/2026-07-17-applicate-toc-pointer-only.md: heading
/// rows used to be a plain <c>Border</c> with only a <c>PointerPressed</c> handler, so a
/// keyboard user had no path to reach or invoke heading navigation and assistive tech saw no
/// invokable role (<c>Focusable=False</c>, <c>NoneAutomationPeer</c>). ApplicateTocPanel now
/// builds each row as a native Avalonia <see cref="Button"/> whose single <c>Click</c> event
/// serves pointer, Enter, and Space.
///
/// <para>These tests exercise the real Avalonia input pipeline (headless Tab/Shift+Tab/Enter/
/// Space key routing, mouse click) against rows built by the panel's own
/// <c>BuildHeadingRow</c> — the same factory the panel's <c>ItemsControl</c> calls in
/// production — rather than asserting "Focusable=true" in isolation. This file's own review
/// found six presence-only tests on this codebase that had already passed for the wrong
/// reason.</para>
///
/// <para>Rows are hosted in a plain <see cref="StackPanel"/>, not through the panel's own
/// <c>ItemsControl</c>/<c>ScrollViewer</c> chrome: this test project's headless
/// <c>Application</c> (<see cref="MarkMello.Applicate.Tests.ApplicateAvaloniaTestApp"/>) has no
/// theme, so <c>ItemsControl</c> never gets a template to realize virtualized containers
/// (confirmed empirically — a forced layout pass over the real panel produced zero row
/// containers). <see cref="StackPanel"/> is not itself templated, so it lays out the directly
/// added rows without needing a theme; the rows themselves are unaffected, since
/// <c>BuildHeadingRow</c> is the SAME method the panel's <see cref="ItemsControl"/> would have
/// called, and the row's own <c>mm-toc-row</c> template/Click wiring comes from
/// <c>MarkMello.Presentation.App.axaml</c>'s merged <c>Controls.axaml</c>, which — unlike
/// <c>ItemsControl</c>'s built-in Fluent/Simple theme template — this test app also merges.</para>
/// </summary>
public sealed class ApplicateTocPanelKeyboardAccessibilityTests
{
    [Fact]
    public async Task TabReachesRowsInDocumentOrderAndShiftTabReverses()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var wired = BuildWiredPanel();
            try
            {
                wired.Rows[0].Focus(NavigationMethod.Tab);
                Assert.True(wired.Rows[0].IsFocused, "Setup: focusing row 0 directly should succeed.");

                PressTab(wired.Window);
                Assert.False(wired.Rows[0].IsFocused);
                Assert.True(wired.Rows[1].IsFocused, "Tab from row 0 should land on row 1 next (document order).");

                PressTab(wired.Window);
                Assert.True(wired.Rows[2].IsFocused, "Tab from row 1 should land on row 2 next (document order).");

                PressTab(wired.Window, shift: true);
                Assert.True(wired.Rows[1].IsFocused, "Shift+Tab from row 2 should return to row 1.");

                PressTab(wired.Window, shift: true);
                Assert.True(wired.Rows[0].IsFocused, "Shift+Tab from row 1 should return to row 0.");
            }
            finally
            {
                wired.Window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PointerClickAndEnterInvokeTheIdenticalCommandPath()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var wired = BuildWiredPanel();
            try
            {
                var clickOutcome = CaptureInteraction(wired.ViewModel, () => ClickRow(wired.Window, wired.Rows[1]));

                // Rewind through the SAME production command (not a direct field
                // write) so the Enter path below is provably what moves
                // ActiveHeadingId, not a no-op re-click landing on the same value.
                wired.ViewModel.ScrollToHeadingCommand.Execute("alpha");

                var enterOutcome = CaptureInteraction(
                    wired.ViewModel,
                    () => PressKeyOnFocusedRow(wired.Window, wired.Rows[1], Key.Enter, PhysicalKey.Enter));

                Assert.Equal(clickOutcome, enterOutcome);
                Assert.Equal("beta", enterOutcome.ActiveHeadingId);
                Assert.Equal("beta", Assert.Single(enterOutcome.RequestedIds));
            }
            finally
            {
                wired.Window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PointerClickAndSpaceInvokeTheIdenticalCommandPath()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var wired = BuildWiredPanel();
            try
            {
                var clickOutcome = CaptureInteraction(wired.ViewModel, () => ClickRow(wired.Window, wired.Rows[2]));

                wired.ViewModel.ScrollToHeadingCommand.Execute("alpha");

                var spaceOutcome = CaptureInteraction(
                    wired.ViewModel,
                    () => PressKeyOnFocusedRow(wired.Window, wired.Rows[2], Key.Space, PhysicalKey.Space));

                Assert.Equal(clickOutcome, spaceOutcome);
                Assert.Equal("gamma", spaceOutcome.ActiveHeadingId);
                Assert.Equal("gamma", Assert.Single(spaceOutcome.RequestedIds));
            }
            finally
            {
                wired.Window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task KeyboardFocusShowsAnAdornerThatPointerFocusDoesNot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            // This test app's Application has no theme (see class remarks), and
            // Window's own template — which is where the AdornerLayer normally
            // lives (Avalonia.Themes.Fluent's VisualLayerManager) — needs one.
            // Add FluentTheme scoped strictly to this test's lifetime (removed in
            // the finally below) rather than touching the shared
            // ApplicateAvaloniaTestApp, which every other test in this assembly
            // also runs against.
            var app = Avalonia.Application.Current!;
            var theme = new FluentTheme();
            app.Styles.Add(theme);
            try
            {
                var wired = BuildWiredPanel();
                try
                {
                    // Structural assertion, not a pixel/colour capture: an
                    // adorner targeting the row exists in its AdornerLayer only
                    // when focus arrived via Tab, matching Avalonia's own
                    // Control.OnGotFocus contract (NavigationMethod.Tab/
                    // Directional show the ring, Pointer/Unspecified do not).
                    wired.Rows[0].Focus(NavigationMethod.Tab);
                    var layer = AdornerLayer.GetAdornerLayer(wired.Rows[0]);
                    Assert.NotNull(layer);
                    Assert.Contains(
                        layer!.Children,
                        child => ReferenceEquals(AdornerLayer.GetAdornedElement(child), wired.Rows[0]));

                    wired.Rows[1].Focus(NavigationMethod.Pointer);
                    Assert.DoesNotContain(
                        layer.Children,
                        child => ReferenceEquals(AdornerLayer.GetAdornedElement(child), wired.Rows[0]));
                    Assert.DoesNotContain(
                        layer.Children,
                        child => ReferenceEquals(AdornerLayer.GetAdornedElement(child), wired.Rows[1]));
                }
                finally
                {
                    wired.Window.Close();
                }
            }
            finally
            {
                app.Styles.Remove(theme);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EachRowsAutomationPeerReportsThatHeadingsOwnLevel()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            // Distinct levels across the whole Markdown h1..h6 domain, and NOT
            // in ascending order, so neither a hardcoded constant nor a
            // row-index-derived value can pass this.
            var rows = BuildRowsAtLevels(3, 1, 6, 2);

            // Read back through the row's own AutomationPeer, which is the
            // object Avalonia.Win32.Automation's AutomationNode.GetPropertyValue
            // queries for UIA_HeadingLevelPropertyId. Asserting
            // AutomationProperties.GetHeadingLevel(row) instead would only read
            // back the property BuildHeadingRow just wrote and would still pass
            // if the peer chain never surfaced it.
            var observed = rows
                .Select(row => ControlAutomationPeer.CreatePeerForElement(row).GetHeadingLevel())
                .ToArray();

            Assert.Equal([3, 1, 6, 2], observed);

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RecyclingPlaceholderRowsPeerAdvertisesNoHeadingLevel()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var panel = new ApplicateTocPanel();
            var placeholder = (Button)BuildHeadingRowMethod.Invoke(panel, [null])!;

            // 0 is the value Avalonia's ToUiaHeadingLevel maps to
            // UiaHeadingLevel.None ("no heading level specified") — the correct
            // advertisement for the null placeholder Avalonia builds while
            // recycling virtualized containers. Guards against a future edit
            // hoisting SetHeadingLevel above BuildHeadingRow's null check and
            // making every recycled container claim to be a heading.
            Assert.Equal(0, ControlAutomationPeer.CreatePeerForElement(placeholder).GetHeadingLevel());

            return 0;
        }, CancellationToken.None);
    }

    private static readonly MethodInfo BuildHeadingRowMethod = typeof(ApplicateTocPanel).GetMethod(
        "BuildHeadingRow",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Button[] BuildRowsAtLevels(params int[] levels)
    {
        var viewModel = MinimalMainWindowViewModelFactory.Create();
        viewModel.UpdateDocumentHeadings(
            levels.Select((level, i) => new DocumentHeading($"h{i}", level, $"Heading {i}", (level - 1) * 12.0)).ToArray());

        var panel = new ApplicateTocPanel();
        panel.DataContext = viewModel;

        // No Window and no layout pass: an automation peer is created by
        // Control.GetOrCreateAutomationPeer, which only requires the UI thread
        // (VerifyAccess) — unlike the focus/adorner tests above, nothing here
        // depends on the row being laid out.
        return viewModel.DocumentHeadings
            .Select(heading => (Button)BuildHeadingRowMethod.Invoke(panel, [heading])!)
            .ToArray();
    }

    private static WiredPanel BuildWiredPanel()
    {
        var viewModel = MinimalMainWindowViewModelFactory.Create();
        viewModel.UpdateDocumentHeadings(new[]
        {
            new DocumentHeading("alpha", 1, "Alpha", 0),
            new DocumentHeading("beta", 1, "Beta", 0),
            new DocumentHeading("gamma", 1, "Gamma", 0),
        });

        var panel = new ApplicateTocPanel();
        // Populate the panel's private _viewModel field the same way
        // production does (DataContext -> AttachViewModel), so each row's
        // Click handler (OnRowClicked), which reads the panel's _viewModel at
        // click time, resolves to this test's view model.
        panel.DataContext = viewModel;

        var buildHeadingRow = typeof(ApplicateTocPanel).GetMethod(
            "BuildHeadingRow",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Button BuildRow(string id)
            => (Button)buildHeadingRow.Invoke(panel, [viewModel.DocumentHeadings.Single(h => h.Id == id)])!;

        var rows = new[] { BuildRow("alpha"), BuildRow("beta"), BuildRow("gamma") };

        var stack = new StackPanel();
        foreach (var row in rows)
        {
            stack.Children.Add(row);
        }

        var window = new Window { Content = stack, Width = 320, Height = 480 };
        window.Show();

        // Materializing layout (Bounds, focus adorner target resolution) needs
        // an actual layout+render pass, not just draining posted
        // continuations — mirrors the loop HeadlessWindowExtensions runs
        // around every simulated input call.
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        return new WiredPanel(window, viewModel, rows);
    }

    private static void ClickRow(Window window, Button row)
    {
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static void PressKeyOnFocusedRow(Window window, Button row, Key key, PhysicalKey physicalKey)
    {
        row.Focus(NavigationMethod.Tab);
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
    }

    private static void PressTab(Window window, bool shift = false)
    {
        var modifiers = shift ? RawInputModifiers.Shift : RawInputModifiers.None;
        window.KeyPress(Key.Tab, modifiers, PhysicalKey.Tab, null);
        window.KeyRelease(Key.Tab, modifiers, PhysicalKey.Tab, null);
    }

    private static InteractionOutcome CaptureInteraction(MainWindowViewModel viewModel, Action interact)
    {
        var requested = new List<string>();
        void Handler(object? _, string id) => requested.Add(id);
        viewModel.ScrollToHeadingRequested += Handler;
        try
        {
            interact();
        }
        finally
        {
            viewModel.ScrollToHeadingRequested -= Handler;
        }
        return new InteractionOutcome(viewModel.ActiveHeadingId, requested.ToArray());
    }

    private sealed record WiredPanel(Window Window, MainWindowViewModel ViewModel, IReadOnlyList<Button> Rows);

    private sealed record InteractionOutcome(string ActiveHeadingId, string[] RequestedIds)
    {
        public bool Equals(InteractionOutcome? other)
            => other is not null
               && ActiveHeadingId == other.ActiveHeadingId
               && RequestedIds.SequenceEqual(other.RequestedIds);

        public override int GetHashCode() => ActiveHeadingId.GetHashCode();
    }
}
