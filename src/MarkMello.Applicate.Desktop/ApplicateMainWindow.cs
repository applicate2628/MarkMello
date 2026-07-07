using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Activation;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation;
using MarkMello.Presentation.ViewModels;
using MarkMello.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MarkMello.Applicate.Desktop;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "_siblingMountBridge is disposed via the Window.Closed event handler — Avalonia Windows have a deterministic close lifecycle and IDisposable on Window is not the appropriate pattern.")]
public sealed class ApplicateMainWindow : MainWindow
{
    // Real bounds so WebView2 initialises correctly; Margin pushes the HWND
    // far enough offscreen that no part of it intersects the visible window
    // even on multi-monitor setups. HorizontalAlignment.Left +
    // VerticalAlignment.Top stop the panel from stretching to fill BodyPanel.
    // Evidence: scratch smoke at .scratch/webview-smoke/run.out.txt verified
    // the MarkMello renderer reaches all readiness gates with viewport
    // 640x360 while parked at Margin=-5000 with these settings.
    private const double WarmupPanelWidth = 1024;
    private const double WarmupPanelHeight = 768;
    private static readonly Thickness WarmupPanelMargin = new(-5000, 0, 0, 0);

    // TOC column resize bounds. While the TOC is visible the column carries
    // these as MinWidth/MaxWidth so Avalonia's GridSplitter clamps the drag to
    // [min, max] live — verified against Avalonia 12 GridSplitter
    // .GetDeltaConstraints, which derives its delta limits from each
    // definition's UserMinSize/UserMaxSize. MinWidth is dropped to 0 when the
    // TOC is hidden because it is a hard floor that would otherwise keep the
    // column 160px wide even with Width=0.
    private const double TocColumnMinWidth = 160;
    private const double TocColumnMaxWidth = 480;
    private const double TocColumnDefaultWidth = 240;

    // The inactive edit-preview prime warms Ctrl+E by rendering the active
    // document on the edit WebView before the user switches modes. Small docs
    // can prime immediately; heavy docs wait until the viewer has committed
    // and had one short post-reveal quiet period, so startup/tab reveal is not
    // taxed but the first edit transition is warmed without a visible pause.
    // Perf (tab-switch reveal stability): 0 = NO document primes the off-screen
    // edit-preview SYNCHRONOUSLY inside the reveal window. Every doc now routes
    // through the delayed ScheduleDelayedHeavyPrime path (~300ms after reveal-ready),
    // so the prime's second full render no longer contends with the viewer reveal on
    // the shared UI/GPU thread -- this trims the cross-switch latency variance. The
    // first Ctrl+E after a switch warms ~300ms later, the already-accepted heavy-doc
    // tradeoff, now applied to all sizes. Edit-mode correctness is unchanged.
    private const int InactiveEditPrimeImmediateMaxDocumentContentLength = 0;
    private const int InactiveEditPrimeVeryHeavyDocumentContentLength = 1024 * 1024;
    private static readonly TimeSpan InactiveEditPrimeHeavyDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan InactiveEditPrimeVeryHeavyDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan SecondaryWebViewPreWarmDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan SecondaryWebViewPreWarmFallbackDelay = TimeSpan.FromSeconds(4);

    private Panel? _tabsContentPanel;
    private ApplicateSiblingMountBridge? _siblingMountBridge;
    private ApplicateModeTransactionHostRouter? _modeTransactionHostRouter;
    private ApplicateDocumentSwitchRevealCoordinator? _viewerDocumentSwitchRevealCoordinator;
    private ApplicateDocumentSwitchRevealCoordinator? _editDocumentSwitchRevealCoordinator;
    private ApplicateThemeSwitchRevealCoordinator? _viewerThemeSwitchRevealCoordinator;
    private ApplicateThemeSwitchRevealCoordinator? _editThemeSwitchRevealCoordinator;
    private bool _editModeHotkeyDown;

    public ApplicateMainWindow(
        MainWindowViewModel viewModel,
        StartupSmokeTestOptions startupSmokeTestOptions,
        ISettingsStore settings,
        ApplicateSingleInstanceService? singleInstance = null)
        : base(viewModel, startupSmokeTestOptions, settings)
    {
        ApplicateTrace.DiagMs("startup-applicate-window", "applicate-ctor-start");
        var holdStartupDocumentReveal = ShouldHoldStartupDocumentReveal();
        if (holdStartupDocumentReveal)
        {
            ApplicateTrace.DiagMs("startup-applicate-window", "startup-window-reveal-gated");
        }
        var viewerTemplate = new ApplicateViewerTemplate();
        DataTemplates.Insert(0, viewerTemplate);
        InstallViewerHostTemplate(viewerTemplate);
        ApplicateTrace.DiagMs("startup-applicate-window", "install-warmup-panel-start");
        InstallSharedWebViewWarmupPanel();
        ApplicateTrace.DiagMs("startup-applicate-window", "install-warmup-panel-end");
        // PE r2 item A: trigger pre-warm IMMEDIATELY after the warmup panel is
        // populated so the shell asset-bundle load, file write, Navigate, and
        // document-ready IPC can overlap with the remaining ctor work (install-
        // tabs, install-sibling-views, WebView2 controller init) and the base
        // class's Window-Opened firing, instead of racing the user's first
        // RequestRender out of the Opened-event reveal at ms~1300.
        InstallSharedWebViewPreWarm();
        ApplicateTrace.DiagMs("startup-applicate-window", "install-tabs-start");
        InstallTabsAndWelcome();
        ApplicateTrace.DiagMs("startup-applicate-window", "install-tabs-end");
        ApplicateTrace.DiagMs("startup-applicate-window", "install-sibling-views-start");
        InstallSiblingMountedViews(
            viewModel,
            skipInitialViewerDocumentSwitchCover: holdStartupDocumentReveal);
        ApplicateTrace.DiagMs("startup-applicate-window", "install-sibling-views-end");
        if (holdStartupDocumentReveal)
        {
            InstallStartupDocumentRevealGate(viewModel);
        }
        MarkStartupDocumentPublishReady();
        InstallHostShortcutBridge(viewModel);
        InstallActiveDocumentBridge(viewModel);
        InstallSingleInstanceActivationBridge(viewModel, singleInstance);
        InstallPopupZOrderFollow(viewModel);
        InstallPopupFadeIn();
        InstallUnifiedScrollBarStyle();
        InstallEditModeDragSuppression(viewModel);
        InstallApplicateRendererPolicy(viewModel);
        RemoveInheritedEditModeKeyBindings();
        InstallEditModeHotkeyRepeatGate(viewModel);
        InstallTabHotkeys();
        Opened += (_, _) => Title = $"{Title} [Applicate overlay]";
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            InstallStatusHintAboveWebView,
            Avalonia.Threading.DispatcherPriority.Loaded);
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => InstallDocumentHealthBannerOverlay(viewModel),
            Avalonia.Threading.DispatcherPriority.Loaded);
        // Start the recurring background update check once the window is shown
        // (the one-shot startup check already runs from InitializeAsync). The VM
        // owns the timer; the matching StopPeriodicUpdateChecks() is in
        // OnApplicateMainWindowClosed so the timer is torn down on close.
        Opened += (_, _) => viewModel.StartPeriodicUpdateChecks();
        ApplicateTrace.DiagMs("startup-applicate-window", "applicate-ctor-end");
    }

    private static void MarkStartupDocumentPublishReady()
    {
        App.Services?
            .GetService<ApplicateRendererReadinessService>()?
            .MarkStartupDocumentPublishReady();
        ApplicateTrace.DiagMs("startup-applicate-window", "startup-document-publish-ready");
    }

    private static bool ShouldHoldStartupDocumentReveal()
    {
        var startupPath = App.Services?.GetService<ICommandLineActivation>()?.GetActivationFilePath();
        if (!string.IsNullOrWhiteSpace(startupPath))
        {
            return true;
        }

        var sessionStore = App.Services?.GetService<IApplicateSessionStore>();
        if (sessionStore is null)
        {
            return false;
        }

        try
        {
            var session = sessionStore.LoadAsync().AsTask().GetAwaiter().GetResult();
            var restoredStartupPath = session.GetStartupDocumentPath();
            return !string.IsNullOrWhiteSpace(restoredStartupPath)
                   && System.IO.File.Exists(restoredStartupPath);
        }
        catch
        {
            return false;
        }
    }

    private void InstallStartupDocumentRevealGate(MainWindowViewModel viewModel)
    {
        var hostProvider = App.Services?.GetService<IApplicateSharedWebViewHostProvider>();
        var viewerHost = hostProvider?.ViewerHost ?? App.Services?.GetService<IApplicateSharedWebViewHost>();
        if (viewerHost is null)
        {
            Opacity = 1;
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-reveal-released",
                "reason=no-viewer-host");
            return;
        }

        var startupViewerHost = viewerHost;
        var startupCover = new ApplicateModeRevealCoverWindow();
        var released = false;
        var startupWindowOpened = false;
        var documentRevealReady = false;
        var waitForHeadings = viewModel.IsTocPreferredVisible;
        var headingsReady = !waitForHeadings || viewModel.HasDocumentHeadings;
        var fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        var rendererSettleFallbackTimer = new DispatcherTimer
        {
            Interval = ApplicateSharedWebViewHost.RendererSettleFallbackTimeout
        };
        var rendererSettleArmed = false;
        var rendererSettleReady = false;
        var rendererSettleReleaseReason = string.Empty;
        Opened += OnStartupWindowOpened;
        SizeChanged += OnStartupWindowSizeChanged;
        startupViewerHost.View.DocumentRevealReady += OnDocumentRevealReady;
        startupViewerHost.View.HeadingsChanged += OnHeadingsChanged;
        rendererSettleFallbackTimer.Tick += OnRendererSettleFallbackTick;
        startupViewerHost.RendererFailed += OnRendererFailed;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
        fallbackTimer.Tick += OnFallbackTick;
        fallbackTimer.Start();

        void OnStartupWindowOpened(object? sender, EventArgs e)
        {
            startupWindowOpened = true;
            QueueStartupCover("opened");
        }

        void OnStartupWindowSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (!startupWindowOpened)
            {
                return;
            }

            QueueStartupCover("size-changed");
        }

        void QueueStartupCover(string reason)
            => Dispatcher.UIThread.Post(
                () =>
                {
                    if (released)
                    {
                        return;
                    }

                    var shown = startupCover.ShowStartupSplash(this, viewModel.Document?.FileName);
                    ApplicateTrace.DiagMs(
                        "startup-applicate-window",
                        "startup-window-cover-shown",
                        $"reason={reason} shown={shown}");
                },
                DispatcherPriority.Render);

        void OnDocumentRevealReady(object? sender, EventArgs e)
            => Dispatcher.UIThread.Post(
                () =>
                {
                    documentRevealReady = true;
                    TryRelease("document-reveal-ready");
                },
                DispatcherPriority.Render);

        void OnHeadingsChanged(object? sender, IReadOnlyList<DocumentHeading> headings)
            => Dispatcher.UIThread.Post(
                () =>
                {
                    waitForHeadings = viewModel.IsTocPreferredVisible && headings.Count > 0;
                    headingsReady = !waitForHeadings || headings.Count > 0;
                    TryRelease("headings-reported");
                },
                DispatcherPriority.Background);

        void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not nameof(MainWindowViewModel.DocumentHeadings)
                and not nameof(MainWindowViewModel.IsTocVisible)
                and not nameof(MainWindowViewModel.HasDocumentHeadings))
            {
                return;
            }

            if (!waitForHeadings)
            {
                return;
            }

            headingsReady = viewModel.HasDocumentHeadings || !viewModel.IsTocPreferredVisible;
            TryRelease("headings-applied");
        }

        void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
            => Dispatcher.UIThread.Post(
                () => Release("renderer-failed"),
                DispatcherPriority.Render);

        void OnFallbackTick(object? sender, EventArgs e)
            => Release("fallback");

        void OnClosed(object? sender, EventArgs e)
        {
            Cleanup();
            startupCover.Dispose();
        }

        void TryRelease(string reason)
        {
            if (!documentRevealReady || (waitForHeadings && !headingsReady))
            {
                return;
            }

            if (ShouldWaitForRendererSettle())
            {
                ArmRendererSettle(reason);
                return;
            }

            ReleaseAfterPaint(reason);
        }

        bool ShouldWaitForRendererSettle()
            => !rendererSettleReady
               && ApplicateSharedWebViewHost.ShouldSkipRendererFrameWait(
                   viewModel.Document,
                   transactionGeneration: 0);

        void ArmRendererSettle(string reason)
        {
            if (rendererSettleArmed)
            {
                return;
            }

            rendererSettleArmed = true;
            rendererSettleReleaseReason = reason;
            startupViewerHost.View.ModeToggleSettled += OnRendererSettled;
            rendererSettleFallbackTimer.Start();
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-renderer-settle-armed",
                $"reason={reason}");
            startupViewerHost.View.RequestModeToggleSettleProbe();
        }

        void OnRendererSettled(object? sender, EventArgs e)
            => Dispatcher.UIThread.Post(
                () => CompleteRendererSettle("ipc-ack"),
                DispatcherPriority.Render);

        void OnRendererSettleFallbackTick(object? sender, EventArgs e)
            => CompleteRendererSettle("fallback-timer");

        void CompleteRendererSettle(string path)
        {
            if (released || !rendererSettleArmed)
            {
                return;
            }

            rendererSettleReady = true;
            ReleaseRendererSettleWait();
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-renderer-settle-complete",
                $"path={path}");
            var reason = string.IsNullOrWhiteSpace(rendererSettleReleaseReason)
                ? path
                : rendererSettleReleaseReason + "-" + path;
            ReleaseAfterPaint(reason);
        }

        void ReleaseRendererSettleWait()
        {
            if (rendererSettleArmed)
            {
                startupViewerHost.View.ModeToggleSettled -= OnRendererSettled;
            }

            rendererSettleArmed = false;
            rendererSettleFallbackTimer.Stop();
        }

        void Release(string reason)
        {
            if (released)
            {
                return;
            }

            released = true;
            Cleanup();
            Opacity = 1;
            Dispatcher.UIThread.Post(
                () => HideStartupCover(reason),
                DispatcherPriority.Render);
        }

        void ReleaseAfterPaint(string reason)
        {
            if (released)
            {
                return;
            }

            released = true;
            Cleanup();
            Opacity = 1;

            // Keep the full-window startup cover aligned with the document-
            // switch cover's paint gate. Otherwise startup can expose the tab
            // chrome while the document region is still under its blank cover.
            var hidden = false;
            var fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            void HideOnce(string releaseReason)
            {
                if (hidden)
                {
                    return;
                }

                hidden = true;
                fallbackTimer.Stop();
                fallbackTimer.Tick -= OnReleaseFallbackTick;
                HideStartupCover(releaseReason);
            }

            void OnReleaseFallbackTick(object? sender, EventArgs e)
                => HideOnce(reason + "-fallback");

            fallbackTimer.Tick += OnReleaseFallbackTick;
            fallbackTimer.Start();

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                HideOnce(reason);
                return;
            }

            topLevel.RequestAnimationFrame(_ =>
            {
                if (hidden)
                {
                    return;
                }

                topLevel.RequestAnimationFrame(_ =>
                    Dispatcher.UIThread.Post(
                        () => HideOnce(reason),
                        DispatcherPriority.Render));
            });
        }

        void HideStartupCover(string reason)
        {
            // Perf B1 (audit 2026-06-04): the startup cover is already paint-gated
            // (DocumentRevealReady + double-RAF before HideStartupCover fires), so
            // the fade-out is dead cosmetic time on every startup, not a mask over
            // unpainted first paint. Zero it for the startup reveal. Scoped to THIS
            // call site only — ApplicateMotion.ModeSwitchDuration still drives the
            // in-session mode-toggle and tab/document-switch covers.
            var duration = TimeSpan.Zero;
            startupCover.Hide(duration);
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-reveal-released",
                $"reason={reason} durationMs={duration.TotalMilliseconds:F0}");
        }

        void Cleanup()
        {
            fallbackTimer.Stop();
            fallbackTimer.Tick -= OnFallbackTick;
            ReleaseRendererSettleWait();
            rendererSettleFallbackTimer.Tick -= OnRendererSettleFallbackTick;
            Opened -= OnStartupWindowOpened;
            SizeChanged -= OnStartupWindowSizeChanged;
            startupViewerHost.View.DocumentRevealReady -= OnDocumentRevealReady;
            startupViewerHost.View.HeadingsChanged -= OnHeadingsChanged;
            startupViewerHost.RendererFailed -= OnRendererFailed;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Closed -= OnClosed;
        }
    }

    // Status-hint Border (Ctrl+O / Ctrl+E / Ctrl+, hotkeys at bottom-right
    // in MainWindow.axaml line 416-500) lives in MainWindow's own visual
    // tree, so the native WebView2 child HWND covers it via Win32 airspace
    // — invisible to the user in both reader and edit mode. Fix: after the
    // window is opened (visual tree fully realized), reparent the Border
    // into a Popup with ShouldUseOverlayLayer="False". Avalonia renders
    // such a popup as a separate transient top-level window on Win32, and
    // top-level windows always stack above their owner's child HWNDs.
    private Avalonia.Controls.Primitives.Popup? _statusHintPopup;
    private Avalonia.Controls.Primitives.Popup? _healthBannerPopup;

    // Ctrl+1..9 activates the open document at that 1-based ordinal index.
    // Browser convention: Ctrl+9 jumps to the LAST tab rather than the 9th
    // (so users with many tabs always have a "go to end" shortcut). The
    // handler is bubble-routed so child controls (TextBox, WebView2 HWND)
    // can still see and own the keystroke if they are focused and want it;
    // we only act when no descendant has handled the event yet.
    private void InstallTabHotkeys()
    {
        AddHandler(KeyDownEvent, OnTabHotkey, RoutingStrategies.Bubble, handledEventsToo: false);
    }

    private void OnTabHotkey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
        {
            return;
        }

        var ordinal = e.Key switch
        {
            Key.D1 => 1,
            Key.D2 => 2,
            Key.D3 => 3,
            Key.D4 => 4,
            Key.D5 => 5,
            Key.D6 => 6,
            Key.D7 => 7,
            Key.D8 => 8,
            Key.D9 => 9,
            _ => 0,
        };
        if (ordinal == 0)
        {
            return;
        }

        if (TryActivateTabOrdinal(ordinal))
        {
            e.Handled = true;
        }
    }

    private static bool TryActivateTabOrdinal(int ordinal)
    {
        if (ordinal <= 0 || ordinal > 9)
        {
            return false;
        }

        var openDocs = App.Services?.GetService<IOpenDocumentsService>();
        if (openDocs is null || openDocs.OpenDocuments.Count == 0)
        {
            return false;
        }

        var docs = openDocs.OpenDocuments;
        var index = ordinal == 9
            ? docs.Count - 1
            : System.Math.Min(ordinal - 1, docs.Count - 1);

        var target = docs[index];
        if (!ReferenceEquals(target, openDocs.ActiveDocument))
        {
            openDocs.Activate(target);
        }

        return true;
    }

    private static int? TryReadHostShortcutTabOrdinal(string combo)
    {
        const string prefix = "ctrl+";
        if (!combo.StartsWith(prefix, System.StringComparison.Ordinal)
            || combo.Length != prefix.Length + 1)
        {
            return null;
        }

        var digit = combo[prefix.Length];
        return digit is >= '1' and <= '9'
            ? digit - '0'
            : null;
    }

    private void InstallEditModeHotkeyRepeatGate(MainWindowViewModel viewModel)
    {
        AddHandler(
            KeyDownEvent,
            (_, e) => OnEditModeHotkeyKeyDown(viewModel, e),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            KeyUpEvent,
            OnEditModeHotkeyKeyUp,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void RemoveInheritedEditModeKeyBindings()
    {
        for (var index = KeyBindings.Count - 1; index >= 0; index--)
        {
            if (KeyBindings[index].Gesture is { Key: Key.E } gesture
                && HasEditModeHotkeyModifier(gesture.KeyModifiers)
                && !gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)
                && !gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                KeyBindings.RemoveAt(index);
            }
        }
    }

    private void OnEditModeHotkeyKeyDown(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (!IsEditModeHotkey(e))
        {
            return;
        }

        e.Handled = true;
        if (_editModeHotkeyDown)
        {
            return;
        }

        _editModeHotkeyDown = true;
        if (viewModel.ToggleEditModeCommand.CanExecute(null))
        {
            viewModel.ToggleEditModeCommand.Execute(null);
        }
    }

    private void OnEditModeHotkeyKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.E or Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin
            || !HasEditModeHotkeyModifier(e.KeyModifiers))
        {
            _editModeHotkeyDown = false;
        }
    }

    private static bool IsEditModeHotkey(KeyEventArgs e)
        => e.Key == Key.E
           && HasEditModeHotkeyModifier(e.KeyModifiers)
           && !e.KeyModifiers.HasFlag(KeyModifiers.Alt)
           && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    private static bool HasEditModeHotkeyModifier(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

    private void InstallStatusHintAboveWebView()
    {
        if (_statusHintPopup is not null)
        {
            return;
        }

        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        if (bodyPanel is null)
        {
            return;
        }

        var statusBorder = bodyPanel.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("mm-status"));
        if (statusBorder is null)
        {
            return;
        }

        // Detach from current parent so we can re-host inside the Popup.
        // The Border was placed as a sibling overlay over BodyPanel with
        // ZIndex=300 — ineffective against WebView2's Win32 airspace.
        if (statusBorder.Parent is Panel currentParent)
        {
            currentParent.Children.Remove(statusBorder);
        }

        _statusHintPopup = new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = bodyPanel,
            Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomRight,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.TopLeft,
            // Keep the bottom-right status hint pinned to the BOTTOM. The default
            // positioner flips it to the TOP when the window's bottom is off-screen,
            // and the flipped position lands on the tab strip. Allow horizontal flip
            // + sliding to stay on-screen, but drop FlipY so it never jumps up onto
            // the tabs — it slides to the visible bottom edge instead.
            PlacementConstraintAdjustment =
                Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideY,
            ShouldUseOverlayLayer = false,
            IsLightDismissEnabled = false,
            OverlayDismissEventPassThrough = true,
            Topmost = false,
            Focusable = false,
            Child = statusBorder
        };

        // Append the Popup itself as a child of bodyPanel so Avalonia keeps
        // it in the logical tree (DataContext inheritance for bindings on
        // the inner Border still works through PlacementTarget anchor).
        bodyPanel.Children.Add(_statusHintPopup);
        _statusHintPopup.IsOpen = true;
    }

    private void InstallDocumentHealthBannerOverlay(MainWindowViewModel viewModel)
    {
        if (_healthBannerPopup is not null)
        {
            return;
        }

        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        if (bodyPanel is null)
        {
            return;
        }

        // Float the health banner as a Popup (Win32 transient top-level via
        // ShouldUseOverlayLayer=false) so it stacks ABOVE the WebView2 child HWND
        // AND never participates in layout — showing/hiding it does not resize or
        // shift the document (an in-layout row jumped the text on toggle).
        var banner = BuildDocumentHealthBanner();
        _healthBannerPopup = new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = bodyPanel,
            Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.Bottom,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.Top,
            PlacementConstraintAdjustment =
                Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideY,
            ShouldUseOverlayLayer = false,
            IsLightDismissEnabled = false,
            OverlayDismissEventPassThrough = true,
            Topmost = false,
            Focusable = false,
            Child = banner,
        };
        bodyPanel.Children.Add(_healthBannerPopup);

        void SyncOpen() => _healthBannerPopup!.IsOpen = viewModel.IsDocumentHealthBannerVisible;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsDocumentHealthBannerVisible))
            {
                SyncOpen();
            }
        };
        SyncOpen();
    }

    // ===========================================================
    // InstallApplicateRendererPolicy — Applicate-side renderer-backend
    // policy. WebView is the only renderer Applicate supports; the native
    // Avalonia markdown renderer (MarkdownDocumentView + CSharpMath) was
    // removed from the Applicate-side delivery pipeline. This policy
    // enforces the WebView-only invariant at the in-memory view-model
    // boundary by coercing every assignment of
    // MainWindowViewModel.SelectedRendererBackend == Native back to
    // WebView. The disk-side coercion of any pre-fork persisted
    // "RendererBackend": "Native" value lives in
    // ApplicateRendererCoercingSettingsStore (registered in Program.cs);
    // together they make the WebView-only invariant true at every
    // observable layer.
    //
    // Permanent policy. The upstream MarkdownRendererBackend.Native enum
    // member stays for upstream MarkMello.Desktop (non-Applicate) builds,
    // but Applicate never lets it reach a renderer.
    //
    // Upstream ReadingSettingsPanelView.axaml already wraps the renderer
    // toggle row in <StackPanel IsVisible="False"> so the segmented
    // control is hidden from the user; this method only needs to defend
    // against programmatic assignment (e.g. settings restore racing the
    // disk-side coercer, future code paths assigning the value).
    // ===========================================================
    private void InstallApplicateRendererPolicy(MainWindowViewModel viewModel)
    {
        // Force WebView whenever Native gets selected (via prefs restore or
        // user click). Setting SelectedRendererBackend = WebView triggers
        // the upstream pipeline; the property setter no-ops if already
        // WebView, so this is also safe on startup.
        if (viewModel.SelectedRendererBackend == MarkdownRendererBackend.Native)
        {
            viewModel.SelectedRendererBackend = MarkdownRendererBackend.WebView;
        }
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainWindowViewModel.SelectedRendererBackend)
                && e.PropertyName != nameof(MainWindowViewModel.IsNativeRendererSelected))
            {
                return;
            }
            if (viewModel.SelectedRendererBackend == MarkdownRendererBackend.Native)
            {
                viewModel.SelectedRendererBackend = MarkdownRendererBackend.WebView;
            }
        };

        // Upstream ReadingSettingsPanelView.axaml line 249 already wraps
        // the renderer row in <StackPanel IsVisible="False"> (see comment
        // at axaml lines 242-248), so the row is hidden at upstream level.
        // The previous fork-side DisableNativeToggleInSettings runtime
        // mutation became a hazard after upstream added a Fonts segmented
        // control above Renderer — the "first 2 mm-segmented-item toggles"
        // heuristic ended up hijacking the Fonts row instead, replacing
        // the font picker with a "WebView (расширенный рендер)" note.
        // Removed entirely; force-WebView above is sufficient.
    }

    private static void InstallEditModeDragSuppression(MainWindowViewModel viewModel)
    {
        // Upstream renders a full-window drop overlay bound to
        // IsDragHovering (MainWindow.axaml:502, the orange-tinted Border).
        // In edit mode the overlay covers the preview pane too, which is
        // misleading because the drop is scoped to the editor textbox.
        // Suppress IsDragHovering whenever we are in edit mode. The
        // textbox keeps its own native drop cursor and our OnEditorDrop
        // still inserts at caret; only the window-wide visual is hidden.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainWindowViewModel.IsDragHovering)
                && e.PropertyName != nameof(MainWindowViewModel.IsEditMode))
            {
                return;
            }
            if (viewModel.IsEditMode && viewModel.IsDragHovering)
            {
                viewModel.IsDragHovering = false;
            }
        };
    }

    private void InstallUnifiedScrollBarStyle()
    {
        // Single source of truth for ScrollBar styling across the whole app
        // (source-pane TextBox in edit mode, preview overlay, native preview,
        // popup ScrollViewers, future surfaces) lives in
        // Themes/ApplicateScrollBars.axaml. Loaded into Application.Styles
        // so the styles propagate everywhere — Window.Styles can miss inner
        // ScrollBars when Fluent's ControlTheme intermediates win, but the
        // application-level scope keeps the rules above ControlTheme defaults.
        //
        // XAML rather than C# Style fluent API because Avalonia's template-
        // part selector resolution (`/template/ Thumb#thumb` and template-
        // replacement Setters on Thumb.Template) is most reliable from the
        // XAML parser.
        //
        // Fork-overlay-safe: the .axaml lives under MarkMello.Applicate
        // .Desktop's avares root; no upstream Themes/Controls.axaml edit.
        var uri = new Uri("avares://MarkMello.Applicate/Themes/ApplicateScrollBars.axaml");
        Avalonia.Application.Current?.Styles.Add(new StyleInclude(uri)
        {
            Source = uri
        });
    }

    private void InstallPopupFadeIn()
    {
        // Smooth open transition for the named popup overlays. Avalonia's
        // Popup pops a PopupRoot window instantly, so we instead animate
        // the popup's Child opacity from 0 to 1 on each Opened event. The
        // Transitions collection is installed once per popup; the fade is
        // triggered by setting Opacity = 1 on a dispatch-back-to-UI tick
        // so Avalonia detects a property change to animate over.
        string[] popupNames = ["AppMenuPanel", "AppSettingsPanel", "AppAboutPanel", "AppUpdatesPanel", "SettingsPanel"];
        foreach (var name in popupNames)
        {
            var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>(name);
            if (popup is null)
            {
                continue;
            }
            popup.Opened += OnTrackedPopupOpened;
        }
    }

    private static async void OnTrackedPopupOpened(object? sender, System.EventArgs e)
    {
        if (sender is not Avalonia.Controls.Primitives.Popup popup || popup.Child is not { } child)
        {
            return;
        }

        // Use Animation.RunAsync to guarantee the fade-in plays even when
        // the popup's Child is freshly attached to the visual tree. A
        // simpler Transitions+Opacity approach fights the popup lifecycle:
        // the Child is attached at the moment Opened fires, and any
        // property change in the same dispatcher tick races against the
        // first layout pass that paints the popup at its final opacity.
        var animation = new Avalonia.Animation.Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(140),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0d),
                    Setters = { new Avalonia.Styling.Setter(Avalonia.Visual.OpacityProperty, 0d) }
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1d),
                    Setters = { new Avalonia.Styling.Setter(Avalonia.Visual.OpacityProperty, 1d) }
                }
            }
        };

        try
        {
            await animation.RunAsync(child).ConfigureAwait(true);
        }
        catch (System.Exception)
        {
            // Animation failure is non-fatal — popup is visible at final
            // opacity regardless. Swallow so the popup never disappears.
            child.Opacity = 1;
        }
    }

    private void InstallPopupZOrderFollow(MainWindowViewModel viewModel)
    {
        // Defensive: upstream MainWindow.OnWindowDeactivated already calls
        // CloseOverlayCommand on Deactivated. We add a fork-side handler so
        // that any edge case where the upstream handler is suppressed (e.g.
        // by event-handling order, exception in another handler, or a future
        // upstream refactor) still closes the popups when the window loses
        // focus. CloseOverlayCommand is idempotent: a second call with no
        // overlay open is a no-op.
        Deactivated += (_, _) =>
        {
            if (viewModel.IsDirtyPromptOpen || !viewModel.HasOpenOverlay)
            {
                return;
            }
            if (viewModel.CloseOverlayCommand.CanExecute(null))
            {
                viewModel.CloseOverlayCommand.Execute(null);
            }
        };
    }

    private void InstallViewerHostTemplate(IDataTemplate viewerTemplate)
    {
        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        var viewerHost = bodyPanel?.Children
            .OfType<ContentControl>()
            .FirstOrDefault(static control => control.GetType() == typeof(ContentControl) && control.Name is null);
        if (viewerHost is null)
        {
            return;
        }

        viewerHost.ContentTemplate = viewerTemplate;
    }

    private void InstallTabsAndWelcome()
    {
        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        var openDocs = App.Services?.GetService<IOpenDocumentsService>();
        if (bodyPanel is null || openDocs is null)
        {
            return;
        }

        // Capture all upstream children and clear so we can wrap them inside
        // a new Grid that has a tabs row above. Preserving each child as-is
        // keeps their existing Avalonia bindings to the upstream VM.
        var existing = new System.Collections.Generic.List<Control>();
        foreach (var child in bodyPanel.Children)
        {
            if (child is Control control)
            {
                existing.Add(control);
            }
        }
        bodyPanel.Children.Clear();

        var contentPanel = new Panel();
        _tabsContentPanel = contentPanel;
        foreach (var control in existing)
        {
            contentPanel.Children.Add(control);
        }

        // Upstream MainWindow already renders its own WelcomeView with logo,
        // Create MD, Open file, and shortcut hints. We only inject the tabs
        // strip above the content; no fork-side welcome panel needed.
        var tabsView = new ApplicateTabsView(openDocs);

        // v0.3.2 — Table of Contents column lives to the left of the
        // document content, spans the full content-area height, and has
        // its own ScrollViewer so mouse-wheel input scrolls the TOC
        // independently of the document. Visibility is composite-bound
        // (IsTocVisible = IsViewer AND user-pref AND has
        // headings). The GridSplitter binds two-way to TocColumnWidth on
        // the VM so the user's resize survives layout passes. When the
        // TOC is hidden the entire toc-column collapses to zero width via
        // the visibility binding, letting the document body take the
        // full row.
        var tocPanel = new ApplicateTocPanel
        {
            // Skip implicit DataContext inheritance — the panel will
            // inherit MainWindow's DataContext (= MainWindowViewModel) by
            // ancestry and its DataContextChanged handler will pick it up.
        };
        var tocColumn = new ColumnDefinition(new GridLength(TocColumnDefaultWidth, GridUnitType.Pixel))
        {
            // Real bounds are applied per-visibility in ApplyFromViewModel; the
            // column starts collapsible (MinWidth 0) until the TOC is shown.
            MinWidth = 0,
            MaxWidth = TocColumnMaxWidth,
        };
        var splitterColumn = new ColumnDefinition(new GridLength(1, GridUnitType.Pixel))
        {
            MinWidth = 0,
            MaxWidth = 9,
        };
        var contentColumn = new ColumnDefinition(new GridLength(1, GridUnitType.Star));

        var tocSplitter = new GridSplitter
        {
            Classes = { "mm-editor-splitter" },
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };
        AttachSplitterDraggingHighlight(tocSplitter);

        // Persist the TOC column width once at drag-end (mirrors the
        // content-width resizer): the live two-way binding writes
        // VM.TocColumnWidth continuously during the drag without touching
        // disk; only the final value is saved here. CommitTocColumnWidth
        // early-returns when nothing changed, so a no-op drag persists nothing.
        tocSplitter.DragCompleted += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.CommitTocColumnWidth();
            }
        };

        var contentGrid = new Grid
        {
            UseLayoutRounding = true,
        };
        contentGrid.ColumnDefinitions.Add(tocColumn);
        contentGrid.ColumnDefinitions.Add(splitterColumn);
        contentGrid.ColumnDefinitions.Add(contentColumn);
        Grid.SetColumn(tocPanel, 0);
        Grid.SetColumn(tocSplitter, 1);
        Grid.SetColumn(contentPanel, 2);
        contentGrid.Children.Add(tocPanel);
        contentGrid.Children.Add(tocSplitter);
        contentGrid.Children.Add(contentPanel);
        // Document-switch cover is mounted later on the document/editor slot,
        // not on contentGrid: the TOC column must stay visible and only replace
        // its row contents when the renderer reports new headings.

        // Visibility wiring: bind TOC panel + splitter to MainWindowViewModel.
        // IsTocVisible composite predicate. The columns themselves stay in
        // the Grid (resizable behaviour requires fixed column slots) but
        // collapse to zero width when the TOC is hidden via a property
        // listener installed below.
        tocPanel.Bind(
            Visual.IsVisibleProperty,
            new Avalonia.Data.Binding(nameof(MainWindowViewModel.IsTocVisible)));
        tocSplitter.Bind(
            Visual.IsVisibleProperty,
            new Avalonia.Data.Binding(nameof(MainWindowViewModel.IsTocVisible)));

        // Bind TocColumn width <-> VM.TocColumnWidth so the user's drag is
        // persisted across mode toggles. We listen to ColumnDefinition.Width
        // changes (set by GridSplitter as the user drags) and write the new
        // value back to the VM; we also listen to VM.TocColumnWidth changes
        // (initial value, future "restore default" paths) and write to
        // ColumnDefinition.Width. Avalonia ColumnDefinition.Width is a
        // GridLength (struct) so we go through manual two-way wiring rather
        // than a Binding (Binding cannot translate double <-> GridLength
        // without a converter).
        InstallTocColumnTwoWayBinding(tocColumn, splitterColumn);

        // Persistent TOC chevron — lives in the tabs row, always visible
        // regardless of IsTocVisible state. Glyph flips between «‹» (TOC
        // open → click to collapse) and «›» (TOC hidden → click to expand)
        // via InstallChevronGlyphTracking. Living in the tabs row keeps it
        // out of the WebView2 NativeControlHost area (where managed visuals
        // can be hidden by the Win32 child window's Z-order) and gives the
        // collapse-then-reopen flow a single anchored affordance.
        var chevronPath = new Avalonia.Controls.Shapes.Path
        {
            Width = 8,
            Height = 10,
            Stretch = Avalonia.Media.Stretch.None,
            StrokeThickness = 1.4,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
            Data = Avalonia.Media.Geometry.Parse("M 6,0 L 0,5 L 6,10"),
        };
        chevronPath.Bind(
            Avalonia.Controls.Shapes.Shape.StrokeProperty,
            chevronPath.GetResourceObservable("MmTextFaintBrush"));
        var chevronButton = new Button
        {
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Content = chevronPath,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(chevronButton, "Hide table of contents (Ctrl+T)");
        chevronButton.Bind(
            Button.CommandProperty,
            new Avalonia.Data.Binding(nameof(MainWindowViewModel.ToggleTocCommand)));

        // Tabs row = [chevron column (Auto), tabsView column (*)]. The
        // chevron sits at the absolute left of the document area, always
        // visible, so collapsing the TOC does not strand the user without
        // a re-open trigger.
        var tabsRow = new Grid
        {
            UseLayoutRounding = true,
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        Grid.SetColumn(chevronButton, 0);
        Grid.SetColumn(tabsView, 1);
        tabsRow.Children.Add(chevronButton);
        tabsRow.Children.Add(tabsView);

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        Grid.SetRow(tabsRow, 0);
        Grid.SetRow(contentGrid, 1);
        grid.Children.Add(tabsRow);
        grid.Children.Add(contentGrid);

        bodyPanel.Children.Add(grid);

        InstallChevronGlyphTracking(chevronPath, chevronButton);
    }

    // Document-health banner: lives in its own grid row between the tabs strip
    // and the document content (z-order-safe — a real layout row above the
    // WebView, not a managed overlay over it). Collapsed it shows the defect
    // count + "preview" + dismiss; expanded it previews the wrapped formulas the
    // fix will join, with confirm/cancel. Auto-height row collapses to nothing
    // when IsDocumentHealthBannerVisible is false.
    private static Control BuildDocumentHealthBanner()
    {
        static Avalonia.Data.Binding B(string path) => new(path);

        var warnIcon = new TextBlock
        {
            Text = "⚠",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        warnIcon.Bind(TextBlock.ForegroundProperty, warnIcon.GetResourceObservable("MmAccentBrush"));

        var summaryText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            FontSize = 12,
        };
        summaryText.Bind(TextBlock.TextProperty, B(nameof(MainWindowViewModel.DocumentHealthBannerText)));
        summaryText.Bind(TextBlock.ForegroundProperty, summaryText.GetResourceObservable("MmTextBrush"));

        var fixButton = new Button
        {
            Classes = { "ghost" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        };
        fixButton.Bind(ContentControl.ContentProperty, B(nameof(MainWindowViewModel.DocumentHealthApplyLabel)));
        fixButton.Bind(Button.CommandProperty, B(nameof(MainWindowViewModel.ApplyDocumentHealthFixCommand)));

        var dismissButton = new Button
        {
            Classes = { "topbar-ghost" },
            Content = "×",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dismissButton.Bind(Button.CommandProperty, B(nameof(MainWindowViewModel.DismissDocumentHealthBannerCommand)));

        var summaryRight = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        summaryRight.Children.Add(fixButton);
        summaryRight.Children.Add(dismissButton);
        DockPanel.SetDock(warnIcon, Dock.Left);
        DockPanel.SetDock(summaryRight, Dock.Right);
        var summaryRow = new DockPanel { LastChildFill = true };
        summaryRow.Children.Add(warnIcon);
        summaryRow.Children.Add(summaryRight);
        summaryRow.Children.Add(summaryText);

        var border = new Border
        {
            Padding = new Thickness(12, 6),
            Margin = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = summaryRow,
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("MmSurfaceElevatedBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("MmAccentSoftBrush"));
        return border;
    }

    private static void AttachSplitterDraggingHighlight(GridSplitter splitter)
    {
        splitter.DragStarted += OnSplitterDragStarted;
        splitter.DragCompleted += OnSplitterDragCompleted;
        splitter.PointerCaptureLost += OnSplitterPointerCaptureLost;
    }

    private static void OnSplitterDragStarted(object? sender, VectorEventArgs e)
        => SetSplitterDraggingState(sender, isDragging: true);

    private static void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
        => SetSplitterDraggingState(sender, isDragging: false);

    private static void OnSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        SetSplitterDraggingState(sender, isDragging: false);
    }

    private static void SetSplitterDraggingState(object? sender, bool isDragging)
    {
        if (sender is Control control)
        {
            control.Classes.Set("dragging", isDragging);
        }
    }

    /// <summary>
    /// Tracks <see cref="MainWindowViewModel.IsTocVisible"/> and flips the
    /// chevron glyph + tooltip so the same affordance reads as "collapse to
    /// the left" when TOC is open («‹») and "expand to the right" when TOC
    /// is collapsed («›»). DataContext may attach asynchronously during
    /// Window construction, so the wiring uses the same defer-on-attach
    /// pattern as <see cref="InstallTocColumnTwoWayBinding"/>.
    /// </summary>
    private void InstallChevronGlyphTracking(
        Avalonia.Controls.Shapes.Path chevronPath,
        Button chevronButton)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            DataContextChanged += DeferredAttach;
            return;
        }

        AttachWiring(vm);

        void DeferredAttach(object? sender, System.EventArgs e)
        {
            if (DataContext is not MainWindowViewModel attachedVm)
            {
                return;
            }
            DataContextChanged -= DeferredAttach;
            AttachWiring(attachedVm);
        }

        void AttachWiring(MainWindowViewModel viewModel)
        {
            Apply(viewModel.IsTocVisible);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsTocVisible))
                {
                    Apply(viewModel.IsTocVisible);
                }
            };
        }

        void Apply(bool isTocVisible)
        {
            // «‹» points left (collapse) when open; «›» points right (expand)
            // when collapsed. Both glyphs are 6-px wide, 10-px tall paths
            // drawn into the same 8x10 Path control — only Data swaps.
            chevronPath.Data = Avalonia.Media.Geometry.Parse(
                isTocVisible
                    ? "M 6,0 L 0,5 L 6,10"
                    : "M 0,0 L 6,5 L 0,10");
            ToolTip.SetTip(
                chevronButton,
                isTocVisible
                    ? "Hide table of contents (Ctrl+T)"
                    : "Show table of contents (Ctrl+T)");
        }
    }

    /// <summary>
    /// Two-way wiring between <see cref="MainWindowViewModel.TocColumnWidth"/>
    /// and the TOC <see cref="ColumnDefinition.Width"/>. Avalonia's
    /// <see cref="ColumnDefinition.Width"/> is a <see cref="GridLength"/>
    /// struct and cannot be bound to a primitive <c>double</c> without a
    /// converter; this helper translates both directions explicitly. Also
    /// collapses the TOC and splitter columns to zero width when the TOC
    /// is hidden so the document body claims the full row.
    /// </summary>
    private void InstallTocColumnTwoWayBinding(ColumnDefinition tocColumn, ColumnDefinition splitterColumn)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            // DataContext may attach asynchronously during Window
            // construction; defer the binding until it lands.
            DataContextChanged += DeferredAttach;
            return;
        }

        AttachWiring(vm);

        void DeferredAttach(object? sender, System.EventArgs e)
        {
            if (DataContext is not MainWindowViewModel attachedVm)
            {
                return;
            }
            DataContextChanged -= DeferredAttach;
            AttachWiring(attachedVm);
        }

        void AttachWiring(MainWindowViewModel viewModel)
        {
            bool suppress = false;

            void ApplyFromViewModel()
            {
                if (suppress)
                {
                    return;
                }
                suppress = true;
                try
                {
                    if (viewModel.IsTocVisible)
                    {
                        // Apply the resize bounds so the GridSplitter clamps the
                        // drag to [min, max]; MinWidth must be set before Width.
                        tocColumn.MinWidth = TocColumnMinWidth;
                        tocColumn.MaxWidth = TocColumnMaxWidth;
                        tocColumn.Width = new GridLength(
                            System.Math.Clamp(viewModel.TocColumnWidth, TocColumnMinWidth, TocColumnMaxWidth),
                            GridUnitType.Pixel);
                        splitterColumn.Width = new GridLength(1, GridUnitType.Pixel);
                    }
                    else
                    {
                        // Drop the hard MinWidth floor first so the column can
                        // actually collapse to zero when the TOC is hidden.
                        tocColumn.MinWidth = 0;
                        tocColumn.Width = new GridLength(0, GridUnitType.Pixel);
                        splitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    }
                }
                finally
                {
                    suppress = false;
                }
            }

            void ApplyFromColumn()
            {
                if (suppress)
                {
                    return;
                }
                if (!viewModel.IsTocVisible)
                {
                    return;
                }
                if (tocColumn.Width.GridUnitType != GridUnitType.Pixel)
                {
                    return;
                }
                suppress = true;
                try
                {
                    var clamped = System.Math.Clamp(tocColumn.Width.Value, TocColumnMinWidth, TocColumnMaxWidth);
                    viewModel.TocColumnWidth = clamped;
                }
                finally
                {
                    suppress = false;
                }
            }

            ApplyFromViewModel();
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.TocColumnWidth)
                    || e.PropertyName == nameof(MainWindowViewModel.IsTocVisible))
                {
                    ApplyFromViewModel();
                }
            };

            tocColumn.GetObservable(ColumnDefinition.WidthProperty)
                .Subscribe(new Avalonia.Reactive.AnonymousObserver<GridLength>(_ => ApplyFromColumn()));
        }
    }

    private void InstallSiblingMountedViews(
        MainWindowViewModel viewModel,
        bool skipInitialViewerDocumentSwitchCover = false)
    {
        var contentPanel = _tabsContentPanel;
        if (contentPanel is null)
        {
            return;
        }

        var viewerHost = contentPanel.Children
            .OfType<ContentControl>()
            .FirstOrDefault(cc => cc.GetType() == typeof(ContentControl) && cc.Name is null);
        if (viewerHost is null)
        {
            return;
        }

        // Viewer slot: Content set once at install to viewModel; resolves to
        // ViewerView via the global ApplicateViewerTemplate registered above.
        // Bridge never changes this — it only flips visibility/enabled/etc.
        var viewerSlot = new ContentControl();

        // Edit slot is a Panel (NOT ContentControl) so its Children are added
        // to the visual tree eagerly at app startup, regardless of the slot's
        // IsVisible state. ContentControl uses ContentPresenter which DELAYS
        // realization of Content visuals until the first measure pass with
        // IsVisible=true — meaning EditPreview.OnAttachedToVisualTree (and
        // therefore the one-time SharedHost.AttachTo reparent) would fire at
        // the moment the user presses Ctrl+E, NOT at app startup. That left
        // the 154ms HWND geometry-lag visible on the first toggle even with
        // permanent mount. Panel.Children.Add realizes the visual subtree
        // immediately, so the reparent runs while editSlot.IsVisible=false
        // (reader is initial state) — HWND geometry lag is invisible.
        var editSlot = new Panel
        {
            IsVisible = false,
            IsHitTestVisible = false,
            UseLayoutRounding = true
        };

        var siblingPanel = new Panel { UseLayoutRounding = true };
        siblingPanel.Children.Add(viewerSlot);
        siblingPanel.Children.Add(editSlot);

        var slotIndex = contentPanel.Children.IndexOf(viewerHost);
        contentPanel.Children.Remove(viewerHost);
        contentPanel.Children.Insert(slotIndex, siblingPanel);

        // Pre-build the EditWorkspaceView + ApplicateEditPreviewView pair ONCE
        // at app startup, with DataContext=null (dormant state). The bridge
        // updates the editWorkspace's DataContext on session changes — no
        // per-toggle template materialization, no reparent.
        var hostProvider = App.Services?.GetService<IApplicateSharedWebViewHostProvider>();
        var viewerHostForMode = hostProvider?.ViewerHost ?? App.Services?.GetService<IApplicateSharedWebViewHost>();
        var editHost = hostProvider?.EditPreviewHost ?? viewerHostForMode;

        // GFM task-list checkbox clicks: the renderer posts task-toggle, the view
        // re-raises it, and the VM flips [ ]/[x] on the source line. Each host
        // stamps its own surface as the toggle's origin — the VM selects the
        // channel leg by the surface that was CLICKED, never by the mode at
        // dispatch time (a message crossing a Ctrl+E boundary in flight must
        // still run its own surface's leg, or the silent-swap premise breaks).
        // Hosts are app-lifetime singletons, so no explicit unsubscribe is needed.
        if (viewerHostForMode is not null)
        {
            viewerHostForMode.View.TaskToggleRequested += (_, e)
                => _ = viewModel.ToggleTaskLineAsync(e.Line, e.Checked, e.Key, TaskToggleOrigin.Viewer);
        }
        if (editHost is not null && !ReferenceEquals(editHost, viewerHostForMode))
        {
            editHost.View.TaskToggleRequested += (_, e)
                => _ = viewModel.ToggleTaskLineAsync(e.Line, e.Checked, e.Key, TaskToggleOrigin.EditPreview);
        }

        ApplicateTrace.DiagMs("startup-synthetic-mount", "construct-edit-preview-start");
        var editPreview = new ApplicateEditPreviewView(editHost);
        ApplicateTrace.DiagMs("startup-synthetic-mount", "construct-edit-preview-end");
        ApplicateTrace.DiagMs("startup-synthetic-mount", "construct-edit-workspace-start");
        var editWorkspace = new EditWorkspaceView
        {
            DataContext = null
        };
        ApplicateTrace.DiagMs("startup-synthetic-mount", "construct-edit-workspace-end");
        ApplicateTrace.DiagMs("startup-synthetic-mount", "replace-preview-start");
        if (!editWorkspace.TryReplacePreviewDocumentView(editPreview))
        {
            // Upstream merge (5c329d8 "sync source and preview scrolling")
            // dropped the `Name="PreviewDocumentFrame"` from the wrapper Border
            // in EditWorkspaceView.axaml:189, so TryReplacePreviewDocumentView
            // (which depends on that name) now always returns false. Locate
            // the wrapper by walking from the still-named MarkdownDocumentView.
            var nativeDocView = editWorkspace.FindControl<MarkdownDocumentView>("PreviewDocumentView");
            if (nativeDocView?.Parent is Border parentBorder)
            {
                // The upstream Border holds the readable-column cap for the
                // native MarkdownDocumentView path: MaxWidth bound to
                // DocumentColumnMaxWidth (constant 964px) + HorizontalAlignment
                // =Center. For the WebView path, that cap is wrong — renderer.js
                // already owns the readable column via AvailableContentWidth,
                // so the outer cap leaves the WebView wrapper stuck at 964px
                // and centered in any wider pane, pushing the WebView2's
                // internal scrollbar 50+ DIPs inward from the pane right edge.
                //
                // Fix: dispose the binding expression first so the LocalValue
                // write below isn't overwritten when the Bridge later sets
                // DataContext = session and the binding re-fires. Avalonia 11
                // has no public ClearBinding; BindingExpressionBase implements
                // IDisposable and disposing tears down the OneWay subscription.
                Avalonia.Data.BindingOperations
                    .GetBindingExpressionBase(parentBorder, Border.MaxWidthProperty)
                    ?.Dispose();
                parentBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
                parentBorder.MaxWidth = double.PositiveInfinity;
                // The XAML Border still carried a DoubleTransition on
                // MaxWidth (legacy column-width animation for the upstream
                // MarkdownDocumentView path). After we set MaxWidth to
                // PositiveInfinity, the transition is no longer meaningful
                // and just costs an animation registration per layout pass.
                // Null it out so no animation infrastructure stays attached
                // to this Border in the WebView path.
                parentBorder.Transitions = null;
                parentBorder.Child = editPreview;
            }
        }
        ApplicateTrace.DiagMs("startup-synthetic-mount", "replace-preview-end");

        // Add the pre-built workspace to editSlot.Children NOW. Panel.Children
        // is eager for LogicalChildren but UserControl-templated descendants
        // (EditWorkspaceView wraps its content via XAML template) only realize
        // their full visual subtree on first MEASURE pass — which Avalonia
        // skips for IsVisible=false ancestors. The probe at 16:31:35.018
        // showed EditPreview.OnAttachedToVisualTree firing 100ms AFTER first
        // editSlot.IsVisible=true, confirming this lazy-realize behaviour.
        //
        // Workaround: temporarily flip editSlot.IsVisible=true, force a
        // measure+arrange pass synchronously to realize the templated
        // hierarchy AND fire OnAttachedToVisualTree on EditPreview (which
        // triggers the one-time SharedHost.AttachTo reparent), then flip
        // back to IsVisible=false. The brief visible window during this
        // synchronous code path does not produce a render frame (Avalonia
        // batches invalidations until next dispatcher tick), so the user
        // never sees edit-mode chrome flashing at startup.
        editSlot.Children.Add(editWorkspace);
        // Synthetic mount: realize templated visual tree NOW. Fires
        // OnAttachedToVisualTree on EditPreview, which triggers the one-time
        // SharedHost.AttachTo reparent so the WebView lives under editSlot
        // before the user ever toggles into edit mode. Bounds here are
        // intrinsic-content (Window has not been measured yet); real
        // window-derived bounds settle on first user toggle when parent
        // grid arranges editSlot for the editor+preview split.
        editSlot.IsVisible = true;
        ApplicateTrace.DiagMs("startup-synthetic-mount", "apply-template-start");
        editSlot.ApplyTemplate();
        editWorkspace.ApplyTemplate();
        editPreview.ApplyTemplate();
        ApplicateTrace.DiagMs("startup-synthetic-mount", "apply-template-end");
        ApplicateTrace.DiagMs("startup-synthetic-mount", "measure-arrange-start");
        editSlot.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
        editSlot.Arrange(new Avalonia.Rect(0, 0, editSlot.DesiredSize.Width, editSlot.DesiredSize.Height));
        ApplicateTrace.DiagMs("startup-synthetic-mount", "measure-arrange-end");
        editSlot.IsVisible = false;
        ApplicateTrace.DiagMs("startup-synthetic-mount", "hide-edit-slot-end");

        _modeTransactionHostRouter?.Dispose();
        _modeTransactionHostRouter = viewerHostForMode is not null && editHost is not null
            ? new ApplicateModeTransactionHostRouter(viewerHostForMode, editHost)
            : null;

        _siblingMountBridge = new ApplicateSiblingMountBridge(
            viewModel,
            viewerSlot,
            editSlot,
            editWorkspace,
            () => viewModel.IsViewer,
            () => viewModel.IsEditMode,
            () => viewModel.EditorSession,
            () => viewModel.Document,
            () => viewModel.ReadingPreferences,
            viewerContent: viewModel,
            transactionHost: _modeTransactionHostRouter,
            modeRevealCoverHost: siblingPanel);

        // Atomic reveal for viewer DOCUMENT switches (tab change, startup,
        // reload): hold a solid cover over the document slot until the new
        // document has committed and painted. The TOC column stays visible and
        // swaps its row model in place.
        if (viewerHostForMode is not null)
        {
            _viewerDocumentSwitchRevealCoordinator = new ApplicateDocumentSwitchRevealCoordinator(
                siblingPanel,
                viewerHostForMode,
                viewModel,
                ApplicateMode.Viewer,
                // Reader surface only: IsViewer is `State == Viewing`, which is
                // also true in edit mode (edit is a sub-mode of Viewing).
                () => viewModel.IsViewer && !viewModel.IsEditMode,
                skipInitialCoverSession: skipInitialViewerDocumentSwitchCover);
            _viewerThemeSwitchRevealCoordinator = new ApplicateThemeSwitchRevealCoordinator(
                siblingPanel,
                viewerHostForMode,
                viewModel,
                () => viewModel.IsViewer && !viewModel.IsEditMode);
        }
        if (editHost is not null)
        {
            _editDocumentSwitchRevealCoordinator = new ApplicateDocumentSwitchRevealCoordinator(
                siblingPanel,
                editHost,
                viewModel,
                ApplicateMode.Edit,
                () => viewModel.IsViewer && viewModel.IsEditMode,
                clearHeadingsOnRendererFailure: false,
                // The edit surface updates content in place (editor + live
                // preview), so a same-path reload (F5 / Ctrl+S) has no covered
                // WebView reveal to resolve a doc-switch cover — skip it instead
                // of stalling on the 8s fallback. A real switch still covers.
                suppressSamePathReloadCover: true);
            _editThemeSwitchRevealCoordinator = new ApplicateThemeSwitchRevealCoordinator(
                siblingPanel,
                editHost,
                viewModel,
                () => viewModel.IsViewer && viewModel.IsEditMode);
        }

        InstallInactiveEditPreviewPrime(
            viewModel,
            contentPanel,
            viewerSlot,
            editSlot,
            editPreview,
            viewerHostForMode,
            editHost);

        Closed += OnApplicateMainWindowClosed;
    }

    private void InstallInactiveEditPreviewPrime(
        MainWindowViewModel viewModel,
        Panel contentPanel,
        Control viewerSlot,
        Panel editSlot,
        ApplicateEditPreviewView editPreview,
        IApplicateSharedWebViewHost? viewerCommitHost,
        IApplicateSharedWebViewHost? editPreviewHost)
    {
        const double inactivePrimeOffscreenX = -100000.0;
        var inactivePrimeTimeout = TimeSpan.FromSeconds(15);
        MarkdownSource? primedDocument = null;
        ReadingPreferences? primedPreferences = null;
        Size primedViewportSize = default;
        MarkdownSource? pendingDocument = null;
        ReadingPreferences? pendingPreferences = null;
        Size pendingViewportSize = default;
        MarkdownSource? revealReadyDocument = null;
        ReadingPreferences? revealReadyPreferences = null;
        bool primeQueued = false;
        bool primeInProgress = false;
        bool delayedHeavyPrimeReady = false;
        MarkdownSource? delayedHeavyPrimeDocument = null;
        ReadingPreferences? delayedHeavyPrimePreferences = null;
        Size delayedHeavyPrimeViewportSize = default;
        (
            bool IsVisible,
            bool IsEnabled,
            bool IsHitTestVisible,
            double Opacity,
            double Width,
            double Height,
            Thickness Margin,
            HorizontalAlignment HorizontalAlignment,
            VerticalAlignment VerticalAlignment
        )? activePrimeRestore = null;
        Avalonia.Threading.DispatcherTimer? primeTimeoutTimer = null;
        Avalonia.Threading.DispatcherTimer? delayedHeavyPrimeTimer = null;

        Opened += OnPrimeSurfaceReady;
        contentPanel.PropertyChanged += OnPrimeSurfaceChanged;
        viewerSlot.PropertyChanged += OnPrimeSurfaceChanged;
        viewModel.PropertyChanged += OnPrimeViewModelChanged;
        editPreview.InactivePrimeRendered += OnInactivePrimeRendered;
        if (viewerCommitHost is not null)
        {
            viewerCommitHost.CommitCompleted += OnViewerHostCommitCompleted;
            viewerCommitHost.View.DocumentRevealReady += OnViewerDocumentRevealReady;
            viewerCommitHost.View.ProgressiveAppendCompleted += OnViewerProgressiveAppendCompleted;
        }

        Closed += OnPrimeClosed;

        QueuePrime();

        void OnPrimeSurfaceReady(object? sender, EventArgs e)
            => QueuePrime();

        void OnPrimeSurfaceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.BoundsProperty || e.Property == Visual.IsVisibleProperty)
            {
                QueuePrime();
            }
        }

        void OnPrimeViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsEditMode)
                && viewModel.IsEditMode
                && primeInProgress)
            {
                CompletePrime(success: false, preserveCurrentVisibility: true);
            }

            if (e.PropertyName is nameof(MainWindowViewModel.Document)
                or nameof(MainWindowViewModel.ReadingPreferences))
            {
                // Invalidate the cached reveal-ready marker for the new doc/prefs, but
                // do NOT QueuePrime here: the viewer has not loaded the new document
                // yet, so a prime would only dead-end at the viewer-loaded gate inside
                // the reveal window. The viewer's DocumentRevealReady / commit re-queues
                // the prime once the doc is actually loaded + revealed.
                revealReadyDocument = null;
                revealReadyPreferences = null;
            }
            else if (e.PropertyName is nameof(MainWindowViewModel.IsTocVisible)
                or nameof(MainWindowViewModel.TocColumnWidth)
                or nameof(MainWindowViewModel.IsEditMode))
            {
                // TOC width / edit-state changes do not reload the document (no fresh
                // reveal-ready fires), so they still re-queue the prime directly.
                QueuePrime();
            }
        }

        void OnPrimeClosed(object? sender, EventArgs e)
        {
            CompletePrime(success: false, preserveCurrentVisibility: false);
            CancelDelayedHeavyPrime();
            Opened -= OnPrimeSurfaceReady;
            contentPanel.PropertyChanged -= OnPrimeSurfaceChanged;
            viewerSlot.PropertyChanged -= OnPrimeSurfaceChanged;
            viewModel.PropertyChanged -= OnPrimeViewModelChanged;
            editPreview.InactivePrimeRendered -= OnInactivePrimeRendered;
            if (viewerCommitHost is not null)
            {
                viewerCommitHost.CommitCompleted -= OnViewerHostCommitCompleted;
                viewerCommitHost.View.DocumentRevealReady -= OnViewerDocumentRevealReady;
                viewerCommitHost.View.ProgressiveAppendCompleted -= OnViewerProgressiveAppendCompleted;
            }

            Closed -= OnPrimeClosed;
        }

        void OnInactivePrimeRendered(object? sender, EventArgs e)
            => CompletePrime(success: true, preserveCurrentVisibility: false);

        void OnViewerHostCommitCompleted(object? sender, ApplicateCommitCompletedEventArgs e)
        {
            if (e.TransactionGeneration != 0 || e.Mode != ApplicateMode.Viewer)
            {
                return;
            }

            QueuePrime();
        }

        void OnViewerDocumentRevealReady(object? sender, EventArgs e)
        {
            if (viewerCommitHost is null
                || viewModel.Document is not { } document
                || !viewerCommitHost.View.HasLoadedDocumentForSource(document)
                || !Equals(viewerCommitHost.View.ReadingPreferences, viewModel.ReadingPreferences))
            {
                return;
            }

            revealReadyDocument = document;
            revealReadyPreferences = viewModel.ReadingPreferences;
            QueuePrime();
        }

        void OnViewerProgressiveAppendCompleted(object? sender, EventArgs e)
            => QueuePrime();

        void QueuePrime()
        {
            if (primeQueued)
            {
                return;
            }

            primeQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    primeQueued = false;
                    TryPrime();
                },
                Avalonia.Threading.DispatcherPriority.Background);
        }

        void TryPrime()
        {
            if (viewModel.IsEditMode || viewModel.Document is not { } document)
            {
                CancelDelayedHeavyPrime();
                return;
            }

            var editWorkspaceSize = ResolveInactiveEditPrimeSize(contentPanel, viewerSlot);
            var viewportSize = ResolveInactiveEditPreviewViewportSize(editWorkspaceSize);
            if (viewportSize.Width <= 0 || viewportSize.Height <= 0)
            {
                return;
            }

            var preferences = viewModel.ReadingPreferences;
            if (ReferenceEquals(primedDocument, document)
                && Equals(primedPreferences, preferences)
                && AreClose(primedViewportSize.Width, viewportSize.Width)
                && AreClose(primedViewportSize.Height, viewportSize.Height))
            {
                CancelDelayedHeavyPrime();
                return;
            }

            if (viewerCommitHost is not null)
            {
                var viewerLoaded = viewerCommitHost.View.HasLoadedDocumentForSource(document);
                var preferencesMatch = Equals(viewerCommitHost.View.ReadingPreferences, preferences);
                if (!viewerLoaded || !preferencesMatch)
                {
                    ApplicateTrace.DiagMs(
                        "pane-seq",
                        "editpreview-inactive-prime-gated",
                        $"source={document.Path} viewerLoaded={viewerLoaded} preferencesMatch={preferencesMatch}");
                    return;
                }
            }

            if (viewerCommitHost is not null
                && ReferenceEquals(viewerCommitHost, editPreviewHost)
                && viewModel.IsViewer)
            {
                // Fallback DI can map reader and edit-preview to the same
                // host. In that shape an inactive prime would reparent the
                // visible reader WebView out of its slot and blank the body.
                // Normal production DI provides a separate edit-preview host,
                // so that path can prime offscreen without stealing reader
                // ownership.
                CancelDelayedHeavyPrime();
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "editpreview-inactive-prime-skipped-active-viewer",
                    $"source={document.Path} sharedHost=True");
                return;
            }

            if (TrySkipViewportOnlyPrime(document, preferences, viewportSize))
            {
                return;
            }

            var isHeavyDocument = document.Content.Length > InactiveEditPrimeImmediateMaxDocumentContentLength;
            if (isHeavyDocument && !IsViewerRevealReadyForPrime(document, preferences))
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "editpreview-inactive-prime-gated-reveal",
                    $"source={document.Path} contentLength={document.Content.Length}");
                return;
            }

            if (isHeavyDocument && IsViewerProgressiveAppendPendingForPrime(document))
            {
                return;
            }

            if (isHeavyDocument
                && !IsDelayedHeavyPrimeReady(document, preferences, viewportSize))
            {
                ScheduleDelayedHeavyPrime(document, preferences, viewportSize);
                return;
            }

            CancelDelayedHeavyPrime();

            if (primeInProgress
                && ReferenceEquals(pendingDocument, document)
                && Equals(pendingPreferences, preferences)
                && AreClose(pendingViewportSize.Width, viewportSize.Width)
                && AreClose(pendingViewportSize.Height, viewportSize.Height))
            {
                return;
            }

            if (primeInProgress)
            {
                CompletePrime(success: false, preserveCurrentVisibility: false);
            }

            var restore = BeginPrimeLayout(editWorkspaceSize);
            if (restore is null)
            {
                return;
            }

            activePrimeRestore = restore;
            primeInProgress = true;
            pendingDocument = document;
            pendingPreferences = preferences;
            pendingViewportSize = viewportSize;
            RestartPrimeTimeout();

            ApplicateTrace.DiagMs(
                "pane-seq",
                "editpreview-inactive-prime-layout",
                $"editWorkspaceSize={editWorkspaceSize.Width:F0}x{editWorkspaceSize.Height:F0} viewport={viewportSize.Width:F0}x{viewportSize.Height:F0} editSlotBounds={editSlot.Bounds.Width:F0}x{editSlot.Bounds.Height:F0}");

            if (!editPreview.PrimeInactiveWebPreview(
                document,
                preferences,
                viewModel.ImageSourceResolver,
                viewportSize))
            {
                CompletePrime(success: false, preserveCurrentVisibility: false);
            }
        }

        bool TrySkipViewportOnlyPrime(MarkdownSource document, ReadingPreferences preferences, Size viewportSize)
        {
            if (editPreviewHost is null
                || ReferenceEquals(viewerCommitHost, editPreviewHost))
            {
                return false;
            }

            var previewPreferences = ApplicateEditPreviewView.CreateWebPreviewPreferences(preferences);
            var previewLoaded = editPreviewHost.View.HasLoadedDocumentForSource(document);
            var preferencesMatch = Equals(editPreviewHost.View.ReadingPreferences, previewPreferences);
            if (!previewLoaded || !preferencesMatch)
            {
                return false;
            }

            var previousViewportSize = primedViewportSize;
            primedDocument = document;
            primedPreferences = preferences;
            primedViewportSize = viewportSize;
            CancelDelayedHeavyPrime();
            ApplicateTrace.DiagMs(
                "pane-seq",
                "editpreview-inactive-prime-skipped-size-only",
                $"source={document.Path} previousViewport={previousViewportSize.Width:F0}x{previousViewportSize.Height:F0} newViewport={viewportSize.Width:F0}x{viewportSize.Height:F0}");
            return true;
        }

        bool IsDelayedHeavyPrimeReady(MarkdownSource document, ReadingPreferences preferences, Size viewportSize)
            => delayedHeavyPrimeReady
                && ReferenceEquals(delayedHeavyPrimeDocument, document)
                && Equals(delayedHeavyPrimePreferences, preferences)
                && AreClose(delayedHeavyPrimeViewportSize.Width, viewportSize.Width)
                && AreClose(delayedHeavyPrimeViewportSize.Height, viewportSize.Height);

        bool IsViewerRevealReadyForPrime(MarkdownSource document, ReadingPreferences preferences)
            => ReferenceEquals(revealReadyDocument, document)
                && Equals(revealReadyPreferences, preferences);

        bool IsViewerProgressiveAppendPendingForPrime(MarkdownSource document)
        {
            if (viewerCommitHost?.View.HasPendingProgressiveAppend != true)
            {
                return false;
            }

            ApplicateTrace.DiagMs(
                "pane-seq",
                "editpreview-inactive-prime-gated-progressive",
                $"source={document.Path}");
            return true;
        }

        void ScheduleDelayedHeavyPrime(MarkdownSource document, ReadingPreferences preferences, Size viewportSize)
        {
            if (ReferenceEquals(delayedHeavyPrimeDocument, document)
                && Equals(delayedHeavyPrimePreferences, preferences)
                && AreClose(delayedHeavyPrimeViewportSize.Width, viewportSize.Width)
                && AreClose(delayedHeavyPrimeViewportSize.Height, viewportSize.Height)
                && delayedHeavyPrimeTimer?.IsEnabled == true)
            {
                return;
            }

            delayedHeavyPrimeDocument = document;
            delayedHeavyPrimePreferences = preferences;
            delayedHeavyPrimeViewportSize = viewportSize;
            delayedHeavyPrimeReady = false;

            var delay = ResolveInactiveEditPrimeDelay(document.Content.Length);
            delayedHeavyPrimeTimer ??= new Avalonia.Threading.DispatcherTimer();
            delayedHeavyPrimeTimer.Interval = delay;
            delayedHeavyPrimeTimer.Tick -= OnDelayedHeavyPrimeTimerTick;
            delayedHeavyPrimeTimer.Tick += OnDelayedHeavyPrimeTimerTick;
            delayedHeavyPrimeTimer.Stop();
            delayedHeavyPrimeTimer.Start();

            ApplicateTrace.DiagMs(
                "pane-seq",
                "editpreview-inactive-prime-delayed-heavy",
                $"source={document.Path} contentLength={document.Content.Length} threshold={InactiveEditPrimeImmediateMaxDocumentContentLength} veryHeavyThreshold={InactiveEditPrimeVeryHeavyDocumentContentLength} delayMs={delay.TotalMilliseconds:F0} viewport={viewportSize.Width:F0}x{viewportSize.Height:F0}");
        }

        TimeSpan ResolveInactiveEditPrimeDelay(int contentLength)
            => contentLength > InactiveEditPrimeVeryHeavyDocumentContentLength
                ? InactiveEditPrimeVeryHeavyDelay
                : InactiveEditPrimeHeavyDelay;

        void OnDelayedHeavyPrimeTimerTick(object? sender, EventArgs e)
        {
            delayedHeavyPrimeTimer?.Stop();
            delayedHeavyPrimeReady = true;
            QueuePrime();
        }

        void CancelDelayedHeavyPrime()
        {
            if (delayedHeavyPrimeTimer is not null)
            {
                delayedHeavyPrimeTimer.Stop();
                delayedHeavyPrimeTimer.Tick -= OnDelayedHeavyPrimeTimerTick;
            }

            delayedHeavyPrimeReady = false;
            delayedHeavyPrimeDocument = null;
            delayedHeavyPrimePreferences = null;
            delayedHeavyPrimeViewportSize = default;
        }

        (
            bool IsVisible,
            bool IsEnabled,
            bool IsHitTestVisible,
            double Opacity,
            double Width,
            double Height,
            Thickness Margin,
            HorizontalAlignment HorizontalAlignment,
            VerticalAlignment VerticalAlignment
        )? BeginPrimeLayout(Size editWorkspaceSize)
        {
            if (editWorkspaceSize.Width <= 0 || editWorkspaceSize.Height <= 0)
            {
                return null;
            }

            var restore = (
                editSlot.IsVisible,
                editSlot.IsEnabled,
                editSlot.IsHitTestVisible,
                editSlot.Opacity,
                editSlot.Width,
                editSlot.Height,
                editSlot.Margin,
                editSlot.HorizontalAlignment,
                editSlot.VerticalAlignment);

            editPreview.BeginInactivePrimeVisibility();
            editSlot.Margin = new Thickness(inactivePrimeOffscreenX, 0, 0, 0);
            editSlot.HorizontalAlignment = HorizontalAlignment.Left;
            editSlot.VerticalAlignment = VerticalAlignment.Top;
            editSlot.Width = editWorkspaceSize.Width;
            editSlot.Height = editWorkspaceSize.Height;
            editSlot.IsEnabled = false;
            editSlot.IsHitTestVisible = false;
            editSlot.Opacity = 1.0;
            editSlot.IsVisible = true;
            editSlot.Measure(editWorkspaceSize);
            editSlot.Arrange(new Rect(inactivePrimeOffscreenX, 0, editWorkspaceSize.Width, editWorkspaceSize.Height));
            editSlot.UpdateLayout();
            editPreview.UpdateLayout();
            return restore;
        }

        void RestartPrimeTimeout()
        {
            ReleasePrimeTimeout();
            primeTimeoutTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = inactivePrimeTimeout
            };
            primeTimeoutTimer.Tick += OnPrimeTimeout;
            primeTimeoutTimer.Start();
        }

        void OnPrimeTimeout(object? sender, EventArgs e)
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "editpreview-inactive-prime-timeout",
                $"pendingSource={pendingDocument?.Path ?? "(null)"} viewport={pendingViewportSize.Width:F0}x{pendingViewportSize.Height:F0}");
            CompletePrime(success: false, preserveCurrentVisibility: viewModel.IsEditMode);
        }

        void ReleasePrimeTimeout()
        {
            if (primeTimeoutTimer is null)
            {
                return;
            }

            primeTimeoutTimer.Stop();
            primeTimeoutTimer.Tick -= OnPrimeTimeout;
            primeTimeoutTimer = null;
        }

        void CompletePrime(bool success, bool preserveCurrentVisibility)
        {
            if (!primeInProgress && activePrimeRestore is null)
            {
                return;
            }

            ReleasePrimeTimeout();

            if (activePrimeRestore is { } restore)
            {
                editSlot.Margin = restore.Margin;
                editSlot.HorizontalAlignment = restore.HorizontalAlignment;
                editSlot.VerticalAlignment = restore.VerticalAlignment;
                editSlot.Width = restore.Width;
                editSlot.Height = restore.Height;
                editSlot.Opacity = restore.Opacity;
                if (!preserveCurrentVisibility)
                {
                    editSlot.IsVisible = restore.IsVisible;
                    editSlot.IsEnabled = restore.IsEnabled;
                    editSlot.IsHitTestVisible = restore.IsHitTestVisible;
                }

                activePrimeRestore = null;
            }

            editPreview.EndInactivePrimeVisibility();
            if (success && pendingDocument is not null)
            {
                primedDocument = pendingDocument;
                primedPreferences = pendingPreferences;
                primedViewportSize = pendingViewportSize;
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "editpreview-inactive-prime-complete",
                    $"source={pendingDocument.Path} viewport={pendingViewportSize.Width:F0}x{pendingViewportSize.Height:F0}");
            }

            primeInProgress = false;
            pendingDocument = null;
            pendingPreferences = null;
            pendingViewportSize = default;
        }
    }

    private static Size ResolveInactiveEditPrimeSize(Panel contentPanel, Control viewerSlot)
    {
        _ = contentPanel;
        var width = double.IsFinite(viewerSlot.Bounds.Width) && viewerSlot.Bounds.Width > 0
            ? viewerSlot.Bounds.Width
            : 0;
        var height = double.IsFinite(viewerSlot.Bounds.Height) && viewerSlot.Bounds.Height > 0
            ? viewerSlot.Bounds.Height
            : 0;
        return new Size(width, height);
    }

    private static Size ResolveInactiveEditPreviewViewportSize(Size editWorkspaceSize)
    {
        const double editSplitterWidth = 1.0;
        const double previewToolbarHeight = 34.0;
        var gutter = ApplicateDocumentLayout.GetWebSlotScrollBarGutter();
        var previewColumnWidth = System.Math.Max(1, (editWorkspaceSize.Width - editSplitterWidth) * 0.5);
        var viewportWidth = System.Math.Max(1, previewColumnWidth - gutter.Left - gutter.Right);
        var viewportHeight = System.Math.Max(1, editWorkspaceSize.Height - previewToolbarHeight);
        return new Size(viewportWidth, viewportHeight);
    }

    private static bool AreClose(double left, double right)
        => System.Math.Abs(left - right) < 0.5;

    private void OnApplicateMainWindowClosed(object? sender, EventArgs e)
    {
        _viewerDocumentSwitchRevealCoordinator?.Dispose();
        _viewerDocumentSwitchRevealCoordinator = null;
        _editDocumentSwitchRevealCoordinator?.Dispose();
        _editDocumentSwitchRevealCoordinator = null;
        _viewerThemeSwitchRevealCoordinator?.Dispose();
        _viewerThemeSwitchRevealCoordinator = null;
        _editThemeSwitchRevealCoordinator?.Dispose();
        _editThemeSwitchRevealCoordinator = null;
        _siblingMountBridge?.Dispose();
        _siblingMountBridge = null;
        _modeTransactionHostRouter?.Dispose();
        _modeTransactionHostRouter = null;
        ApplicateWebMarkdownDocumentView.HostShortcutHandler = null;
        (DataContext as MainWindowViewModel)?.StopPeriodicUpdateChecks();
        Closed -= OnApplicateMainWindowClosed;
    }

    // Bridge JS keyhandler ↔ MainWindowViewModel commands. WebView2 captures
    // keyboard focus when the user clicks inside the rendered document, which
    // blocks window-level KeyBindings declared in MainWindow.axaml. The
    // renderer's wireHostShortcuts posts a host-shortcut message; this maps
    // the combo string to the matching command on MainWindowViewModel.
    private void InstallHostShortcutBridge(MainWindowViewModel viewModel)
    {
        ApplicateWebMarkdownDocumentView.HostShortcutHandler = combo =>
        {
            var tabOrdinal = TryReadHostShortcutTabOrdinal(combo);
            if (tabOrdinal.HasValue)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    TryActivateTabOrdinal(tabOrdinal.Value);
                });
                return;
            }

            var command = combo switch
            {
                "ctrl+e" => viewModel.ToggleEditModeCommand,
                "ctrl+o" => viewModel.OpenFileCommand,
                "ctrl+s" => viewModel.SaveCommand,
                "ctrl+shift+s" => viewModel.SaveAsCommand,
                "ctrl+n" => viewModel.CreateNewDocumentCommand,
                "ctrl+r" => viewModel.ReloadCommand,
                "f5" => viewModel.ReloadCommand,
                "escape" => viewModel.ClearErrorCommand,
                "ctrl+t" => viewModel.ToggleTocCommand,
                _ => null
            };
            if (command is not null && command.CanExecute(null))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (command.CanExecute(null))
                    {
                        command.Execute(null);
                    }
                });
            }
        };
    }

    private void InstallSingleInstanceActivationBridge(
        MainWindowViewModel viewModel,
        ApplicateSingleInstanceService? singleInstance)
    {
        if (singleInstance is null)
        {
            return;
        }

        singleInstance.ActivationRequested += (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                ApplicateForegroundWindowActivator.ActivateExternalRequest(this);

                if (args.FilePaths.Count == 0)
                {
                    return;
                }

                var openDocs = App.Services?.GetService<IOpenDocumentsService>();
                if (openDocs is not null)
                {
                    foreach (var path in args.FilePaths)
                    {
                        try
                        {
                            await openDocs.OpenAsync(path).ConfigureAwait(true);
                        }
                        catch (System.IO.IOException)
                        {
                            // File disappeared between secondary-process probe and primary open.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Keep the existing window alive; the activation just fails closed.
                        }
                    }
                    ApplicateForegroundWindowActivator.ActivateExternalRequest(this);
                    return;
                }

                var lastPath = args.FilePaths[^1];
                try
                {
                    await viewModel.OpenPathAsync(lastPath).ConfigureAwait(true);
                    ApplicateForegroundWindowActivator.ActivateExternalRequest(this);
                }
                catch (System.IO.IOException)
                {
                    // Same failure mode as normal open; no extra surface in the activation path.
                }
            });
        };
    }

    private static OpenDocument? FindOpenDocumentByPath(IOpenDocumentsService openDocs, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var doc in openDocs.OpenDocuments)
        {
            if (string.Equals(doc.FilePath, path, System.StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
        }

        return null;
    }

    private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)
    {
        var openDocs = App.Services?.GetService<IOpenDocumentsService>();
        if (openDocs is null)
        {
            return;
        }

        // In-place update channel (task-toggle). Commit: a VERIFIED flip moves
        // every snapshot silently — shared hosts (Source swap, no render) and
        // the open-docs mirror — so nothing repaints and the scroll never
        // moves. Revert: a refusal with unchanged disk sets the ONE checkbox
        // back surgically (a value-equal reload would no-op and leave the DOM
        // lying). Hosts are app-lifetime singletons: no unsubscribe needed.
        var channelHostProvider = App.Services?.GetService<IApplicateSharedWebViewHostProvider>();
        var channelViewerHost = channelHostProvider?.ViewerHost
            ?? App.Services?.GetService<IApplicateSharedWebViewHost>();
        var channelEditHost = channelHostProvider?.EditPreviewHost;
        viewModel.TaskToggleCommitted += (_, commit) =>
        {
            // Viewer surface: its DOM received the user's click, so the silent
            // swap's premise already holds.
            channelViewerHost?.CommitInPlaceSourceSwap(commit.Source);
            if (channelEditHost is not null && !ReferenceEquals(channelEditHost, channelViewerHost))
            {
                // Edit-preview surface: a DISTINCT WebView whose primed DOM
                // never saw the click — patch its one checkbox surgically
                // FIRST so the swap's premise ("DOM already shows this
                // content") becomes true, THEN swap. Keeps the prime warm
                // (zero re-render on the next Ctrl+E) and truthful.
                channelEditHost.View.SetTaskCheckboxState(commit.Line, commit.Checked, commit.Source.Path);
                channelEditHost.CommitInPlaceSourceSwap(commit.Source);
            }

            var mirrored = FindOpenDocumentByPath(openDocs, commit.Source.Path);
            if (mirrored is not null
                && !string.Equals(mirrored.SourceText, commit.Source.Content, System.StringComparison.Ordinal))
            {
                openDocs.UpdateSourceText(mirrored, commit.Source.Content);
            }
        };
        viewModel.TaskToggleDomRevertRequested += (_, revert) =>
            channelViewerHost?.View.SetTaskCheckboxState(revert.Line, revert.Checked, revert.Path);

        // Edit-originated toggle: the edit-preview DOM received the click, so
        // the silent swap's premise already holds there. The flip is an
        // UNSAVED buffer edit — the viewer snapshot and the open-docs mirror
        // stay at disk content (the dirty/save flow owns them).
        viewModel.EditPreviewTaskToggleCommitted += (_, commit) =>
            channelEditHost?.CommitInPlaceSourceSwap(commit.Source);
        viewModel.EditPreviewTaskToggleRevertRequested += (_, revert) =>
            channelEditHost?.View.SetTaskCheckboxState(revert.Line, revert.Checked, revert.Path);

        // Bidirectional sync between IOpenDocumentsService (tabs strip source
        // of truth) and the upstream `MainWindowViewModel.Document` value
        // (what actually renders). Flags prevent the two paths from ping-
        // ponging when an open or close cascades through both sides.
        //
        // Every VM mutation happens on the Avalonia UI thread because
        // ObservableProperty writes touch AvaloniaObject styled properties
        // (which assert thread affinity). Service events may originate on
        // a thread-pool continuation of File.ReadAllTextAsync, so we hop
        // back through Dispatcher.UIThread.Post before invoking the VM.
        var inServiceLoad = false;
        var inVmMirror = false;
        // Non-null while a dirty-prompted tab switch is awaiting Save / Discard /
        // Cancel. The Document-mirror must not re-Activate any OTHER document in
        // that window (a Save resolution publishes the OLD doc via
        // ApplySavedDocument before the queued switch runs, which would flip the
        // tab strip back and leave tabs/editor split-brained).
        OpenDocument? pendingDirtySwitchTarget = null;

        // Last non-null active FILE path — preserved so a session save while a
        // session-only untitled owns the window (ActiveDocument cleared to null)
        // still records the last real file to restore, not the first tab.
        string? lastActivePath = null;

        // A session-only untitled document (created by Ctrl+N) owns the window:
        // VM.Document is null (no file rendered) AND the editor session is the
        // untitled one (CurrentPath is null — set to a real path only on load/
        // save). In this state no OPEN FILE is active, so the null-mirror must not
        // treat the Document clear as a tab close, and no file tab may stay
        // highlighted.
        bool UntitledSessionOwnsWindow()
            => viewModel.Document is null && viewModel.EditorSession is { CurrentPath: null };

        static double NormalizeScrollProgress(double progress)
            => double.IsFinite(progress) ? System.Math.Clamp(progress, 0, 100) : 0;

        void ApplyOpenedDocumentInPlaceWithScroll(OpenDocument activeDocument)
        {
            var progress = NormalizeScrollProgress(activeDocument.ScrollProgressPercent);
            var nextSource = new MarkdownSource(
                activeDocument.FilePath,
                activeDocument.DisplayName,
                activeDocument.SourceText);

            viewModel.ReadingProgress = progress;
            viewModel.ApplyOpenedDocumentInPlace(nextSource);
            viewModel.ReadingProgress = progress;
        }

        openDocs.ActiveDocumentChanged += (_, args) =>
        {
            if (inVmMirror)
            {
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (args.ActiveDocument is null)
                {
                    // A session-only untitled document owns the window: clearing
                    // the active FILE tab is NOT a close — do not run CloseFile
                    // (which would destroy the untitled draft). Just let the tabs
                    // strip show no highlighted file tab.
                    if (UntitledSessionOwnsWindow())
                    {
                        return;
                    }
                    if (viewModel.CloseFileCommand.CanExecute(null))
                    {
                        viewModel.CloseFileCommand.Execute(null);
                    }
                    return;
                }

                // A tab click while the unsaved-changes prompt is already open:
                // RunWithDirtyCheckAsync would drop the action anyway (early
                // return on IsDirtyPromptOpen), but the service has already
                // committed the click — snap the tab strip back to the document
                // the editor still holds so the highlight stays truthful while
                // the user resolves the prompt. (The prompt scrim does NOT cover
                // the tab strip, so this is an ordinary click, not a rare race.)
                if (viewModel.IsDirtyPromptOpen)
                {
                    var editorDoc = FindOpenDocumentByPath(
                        openDocs,
                        viewModel.EditorSession?.CurrentPath ?? viewModel.Document?.Path);
                    if (editorDoc is not null
                        && !ReferenceEquals(openDocs.ActiveDocument, editorDoc))
                    {
                        inVmMirror = true;
                        try
                        {
                            openDocs.Activate(editorDoc);
                        }
                        finally
                        {
                            inVmMirror = false;
                        }
                    }
                    else if (editorDoc is null
                        && UntitledSessionOwnsWindow()
                        && openDocs.ActiveDocument is not null)
                    {
                        // Same click, but the editor holds a session-only untitled
                        // (no path -> editorDoc null): there is no file tab to snap
                        // back to, so clear the active file instead. Otherwise the
                        // tab the user just clicked stays highlighted over the
                        // untitled for the whole time the prompt is open. Same
                        // inVmMirror latch as the snap-back above.
                        inVmMirror = true;
                        try
                        {
                            openDocs.ClearActive();
                        }
                        finally
                        {
                            inVmMirror = false;
                        }
                    }

                    return;
                }

                var newPath = args.ActiveDocument.FilePath;
                var currentPath = viewModel.Document?.Path;
                if (string.Equals(currentPath, newPath, System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Multi-tab startup-scaling polish: if the activated tab
                // is a lazy STUB (created by OpenStubAsync during session-
                // restore, contents not yet read), fill its SourceText
                // from EarlyDocumentCache or disk before anything reads
                // it. Without this, the edit-mode branch below would
                // publish an empty MarkdownSource into the editor and
                // the reader branch would set VM.Document from the cache
                // but leave the OpenDocument.SourceText stale for any
                // later cross-source dedup checks.
                if (!args.ActiveDocument.IsLoaded)
                {
                    try
                    {
                        await openDocs.EnsureLoadedAsync(args.ActiveDocument)
                            .ConfigureAwait(true);
                    }
                    catch (System.IO.IOException)
                    {
                        // File became unreadable since the session
                        // recorded it. Fall through; the reader-mode
                        // OpenPathAsync below will surface the typed
                        // error and either way the welcome state is
                        // reached.
                    }
                    catch (System.UnauthorizedAccessException)
                    {
                        // Same fallthrough as IOException.
                    }
                }

                // Edit mode path: do NOT call OpenPathAsync because its
                // internal ApplyLoadedDocument sets IsEditMode = false,
                // unmounting the EditWorkspace and momentarily flashing
                // reader mode before we re-toggle back to edit. Use the
                // in-place variant instead — it updates Document AND
                // RenderedDocument AND _currentPath AND State AND
                // EditorSession in one pass, keeping IsEditMode=true so
                // the edit workspace stays mounted throughout. The
                // previous code only set Document + session.ApplyLoaded,
                // leaving RenderedDocument stale. On leave-edit Bridge
                // would show the viewer at the new tab's title but with
                // the OLD tab's RenderedDocument painted — visible as
                // "tabs and file don't match" desync (user-reported).
                if (viewModel.IsEditMode && viewModel.EditorSession is not null)
                {
                    var target = args.ActiveDocument;

                    // The tab we are leaving = the document the editor holds.
                    var previous = FindOpenDocumentByPath(
                        openDocs, viewModel.EditorSession.CurrentPath);

                    // Audit H2 guard: the stub load above failed (swallowed
                    // IOException / UnauthorizedAccessException) — its
                    // SourceText is still EMPTY. Applying it would show an
                    // empty editor for a file with real on-disk content, and
                    // a later Ctrl+S would truncate the file. Bail WITHOUT
                    // overwriting: keep the current session, snap the tab
                    // strip back, and tell the user. (Do NOT route through
                    // OpenPathAsync here — its failure path clears
                    // IsEditMode/EditorSession, killing any draft.)
                    if (!target.IsLoaded)
                    {
                        viewModel.NotifyActiveTabLoadFailed(target.DisplayName);
                        if (previous is not null
                            && !ReferenceEquals(openDocs.ActiveDocument, previous))
                        {
                            inVmMirror = true;
                            try
                            {
                                openDocs.Activate(previous);
                            }
                            finally
                            {
                                inVmMirror = false;
                            }
                        }

                        return;
                    }

                    // Audit Critical #1: route the swap through the same
                    // unsaved-changes prompt used by close/reload/open. Clean
                    // editor -> runs immediately (behavior unchanged). Dirty
                    // editor -> queues the swap behind Save / Discard / Cancel
                    // instead of silently overwriting the draft. The service
                    // already committed the click, so Cancel snaps the tab
                    // strip back to the previous document.
                    //
                    // When a DIRTY session-only untitled owns the window, that
                    // queued prompt leaves the just-clicked file tab highlighted
                    // over the untitled for the whole time it is open. Clear the
                    // active file first so no tab is highlighted behind the draft.
                    // Gate on IsDirty (mirrors the VM's private
                    // RequiresDirtyResolution = IsEditMode && EditorSession.IsDirty):
                    // a CLEAN untitled switches immediately with no prompt, and an
                    // unconditional clear there would add a null->target
                    // double-Rebuild flicker on every clean switch. Hold the
                    // inVmMirror latch (as P1/P2/P4 do) so this null activation does
                    // not re-enter the handler and reach the CloseFile branch.
                    if (UntitledSessionOwnsWindow()
                        && viewModel.EditorSession.IsDirty
                        && openDocs.ActiveDocument is not null)
                    {
                        inVmMirror = true;
                        try
                        {
                            openDocs.ClearActive();
                        }
                        finally
                        {
                            inVmMirror = false;
                        }
                    }

                    pendingDirtySwitchTarget = target;
                    await viewModel.RequestDocumentSwitchWithDirtyCheckAsync(
                        () =>
                        {
                            // ONE posted reconciler resolves the switch. A Save
                            // resolution publishes the OLD document (via
                            // ApplySavedDocument) and its Document-mirror lambda
                            // is POSTED before this queued switch runs — so the
                            // suppression flag must stay up until that post has
                            // drained, and only then may the target be
                            // re-asserted. Clearing the flag synchronously here
                            // would let the drained mirror re-activate the old
                            // tab (tabs/editor split-brain, fable acceptance
                            // must-fix). Posts drain only after this synchronous
                            // action completes, so posting BEFORE the apply
                            // keeps the same FIFO (mirror → reconciler) AND
                            // guarantees the flag is released even if the apply
                            // throws.
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                pendingDirtySwitchTarget = null;
                                if (ReferenceEquals(openDocs.ActiveDocument, target))
                                {
                                    return;
                                }

                                inVmMirror = true;
                                try
                                {
                                    openDocs.Activate(target);
                                }
                                finally
                                {
                                    inVmMirror = false;
                                }
                            });

                            inServiceLoad = true;
                            try
                            {
                                ApplyOpenedDocumentInPlaceWithScroll(target);
                            }
                            finally
                            {
                                inServiceLoad = false;
                            }

                            return System.Threading.Tasks.Task.CompletedTask;
                        },
                        onCancel: () =>
                        {
                            // User kept the draft: the editor stays on `previous`,
                            // so the tab strip must revert to it as well. When an
                            // untitled session owns the window there is NO `previous`
                            // file to revert to (CurrentPath is null) — clear the
                            // active file instead, so the tab the user clicked does
                            // not stay highlighted behind the kept untitled draft.
                            pendingDirtySwitchTarget = null;
                            if (previous is null)
                            {
                                if (UntitledSessionOwnsWindow()
                                    && openDocs.ActiveDocument is not null)
                                {
                                    inVmMirror = true;
                                    try
                                    {
                                        openDocs.ClearActive();
                                    }
                                    finally
                                    {
                                        inVmMirror = false;
                                    }
                                }

                                return;
                            }

                            if (ReferenceEquals(openDocs.ActiveDocument, previous))
                            {
                                return;
                            }

                            inVmMirror = true;
                            try
                            {
                                openDocs.Activate(previous);
                            }
                            finally
                            {
                                inVmMirror = false;
                            }
                        }).ConfigureAwait(true);
                    return;
                }

                // Reader-mode tab switch: the open-doc service already owns
                // the activated tab's text once EnsureLoadedAsync succeeds.
                // Apply it in-place so every activation avoids the
                // OpenPathAsync disk/read pipeline; keep OpenPathAsync only
                // as the typed-error fallback when a restored stub could not
                // be materialized.
                //
                // Stale-activation guard: this handler is posted async (rapid
                // A->B->A queues several lambdas), and EnsureLoadedAsync above may
                // have awaited. If a newer activation has since superseded this one
                // (openDocs.ActiveDocument moved on), applying args.ActiveDocument
                // now would paint the wrong document under the now-current tab
                // (tabs show B, doc area shows A). Skip it — the current
                // activation's own posted lambda applies the correct document.
                if (!ReferenceEquals(args.ActiveDocument, openDocs.ActiveDocument))
                {
                    return;
                }

                var wasEditMode = viewModel.IsEditMode;
                inServiceLoad = true;
                try
                {
                    if (args.ActiveDocument.IsLoaded)
                    {
                        ApplyOpenedDocumentInPlaceWithScroll(args.ActiveDocument);
                    }
                    else
                    {
                        await viewModel.OpenPathAsync(newPath).ConfigureAwait(true);
                        // A newer activation may have superseded us during the
                        // async load; if so, the correct doc's own lambda will
                        // re-apply it, so stop rather than leave this stale one.
                        if (!ReferenceEquals(args.ActiveDocument, openDocs.ActiveDocument))
                        {
                            return;
                        }
                    }

                    if (wasEditMode
                        && !viewModel.IsEditMode
                        && viewModel.ToggleEditModeCommand.CanExecute(null))
                    {
                        viewModel.ToggleEditModeCommand.Execute(null);
                    }
                }
                finally
                {
                    inServiceLoad = false;
                }
            });
        };

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.ReadingProgress))
            {
                var active = openDocs.ActiveDocument;
                if (active is not null)
                {
                    openDocs.UpdateState(active, active.EditorCaret, viewModel.ReadingProgress);
                }
                return;
            }

            if (args.PropertyName == nameof(MainWindowViewModel.IsDirty))
            {
                // Mirror the active document's dirty state onto its tab so the
                // strip can paint a dirty marker (OpenDocument.IsModified was
                // otherwise never set true).
                var active = openDocs.ActiveDocument;
                if (active is not null)
                {
                    openDocs.SetModified(active, viewModel.IsDirty);
                }
                return;
            }

            if (args.PropertyName != nameof(MainWindowViewModel.Document))
            {
                return;
            }

            // Scan the freshly-loaded document for repairable math defects
            // (wrapped inline $…$) so the health banner can offer a fix. Runs on
            // every document change including service-driven tab loads, so it
            // sits before the inServiceLoad early-return.
            viewModel.AnalyzeCurrentDocumentHealth();

            if (inServiceLoad)
            {
                return;
            }

            var document = viewModel.Document;
            var path = document?.Path;
            if (string.IsNullOrEmpty(path))
            {
                // VM cleared its document. If the user routed a tab close
                // through CloseFileCommand and the dirty prompt resolved
                // with Save/Discard, the service still holds that doc.
                // Mirror the VM clear by closing the active OpenDocument so
                // the tabs strip matches. If the user clicked Cancel, the
                // VM keeps its document and this branch is never entered.
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    // CreateNewDocument (Ctrl+N) also clears VM.Document before it
                    // installs the untitled EditorSession, but a session-only
                    // untitled now owns the window — NOT a close. Clear the active
                    // FILE tab (so none stays highlighted while the editor shows
                    // the untitled) WITHOUT closing, then bail.
                    //
                    // Snapshot/restore inVmMirror rather than forcing it false: a
                    // concurrent Save-As mirror (the non-null-Document branch
                    // below) may be parked on an await while holding this same
                    // latch (dirty untitled -> Ctrl+N -> Save-As posts that mirror
                    // FIRST, then this one). Clobbering the latch to false would let
                    // that mirror's post-await SetActive re-enter UNSUPPRESSED and
                    // swallow the freshly-created untitled draft.
                    if (UntitledSessionOwnsWindow())
                    {
                        var priorMirror = inVmMirror;
                        inVmMirror = true;
                        try
                        {
                            openDocs.ClearActive();
                        }
                        finally
                        {
                            inVmMirror = priorMirror;
                        }
                        return;
                    }

                    // Defensive: a real CloseFile leaves EditorSession null before
                    // it nulls Document, so this never fires today; kept so any
                    // future Document-null-with-a-live-session path is still not
                    // read as a tab close.
                    if (viewModel.EditorSession is not null)
                    {
                        return;
                    }

                    var active = openDocs.ActiveDocument;
                    if (active is null)
                    {
                        return;
                    }
                    inVmMirror = true;
                    try
                    {
                        openDocs.Close(active);
                    }
                    finally
                    {
                        inVmMirror = false;
                    }

                    // After Close, the service may have promoted a neighbor
                    // as the new active document. The ActiveDocumentChanged
                    // event for that promotion fires while inVmMirror == true
                    // (still in the close call stack), so the normal bridge
                    // path skips it and VM.Document stays null — leaving the
                    // user at the welcome screen instead of the neighbor.
                    // Catch up here: if a neighbor became active, mirror it
                    // back to the VM explicitly.
                    var promoted = openDocs.ActiveDocument;
                    if (promoted is null)
                    {
                        return;
                    }

                    // Pre-set Document + State synchronously from the open
                    // document's cached source so the welcome view does not
                    // render between the VM.Document = null tick and the
                    // (async) OpenPathAsync completion below. The full async
                    // load still runs to refresh RenderedDocument, but by
                    // then State is already Viewing and Document is non-null,
                    // so IsWelcome stays false and the welcome panel never
                    // becomes visible.
                    viewModel.Document = new MarkdownSource(
                        promoted.FilePath,
                        promoted.DisplayName,
                        promoted.SourceText);
                    viewModel.State = MarkMello.Presentation.ViewModels.ViewState.Viewing;

                    // OpenPathAsync calls LoadDocumentAsync with
                    // preserveEditModeAfterLoad: false which sets
                    // IsEditMode = false. When the user closes a tab while
                    // in edit mode, that boots them into reader mode.
                    // Snapshot and restore.
                    var wasInEditMode = viewModel.IsEditMode;

                    inServiceLoad = true;
                    try
                    {
                        await viewModel.OpenPathAsync(promoted.FilePath).ConfigureAwait(true);
                    }
                    catch (System.IO.IOException)
                    {
                        // Neighbor file became unreadable between close and
                        // reopen; user stays at welcome.
                    }
                    finally
                    {
                        inServiceLoad = false;
                    }

                    if (wasInEditMode && !viewModel.IsEditMode)
                    {
                        viewModel.IsEditMode = true;
                    }
                });
                return;
            }
            var fileName = document!.FileName;
            var content = document.Content;

            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                // If service already knows about this path, just activate it.
                // Otherwise also try a cross-source content+filename match
                // so that dropping the same physical file via WebView (temp
                // path) and Native (real path) produces a single tab.
                OpenDocument? known = null;
                foreach (var doc in openDocs.OpenDocuments)
                {
                    if (string.Equals(doc.FilePath, path, System.StringComparison.OrdinalIgnoreCase))
                    {
                        known = doc;
                        break;
                    }
                }

                if (known is null)
                {
                    foreach (var doc in openDocs.OpenDocuments)
                    {
                        if (string.Equals(doc.DisplayName, fileName, System.StringComparison.OrdinalIgnoreCase)
                            && string.Equals(doc.SourceText, content, System.StringComparison.Ordinal))
                        {
                            known = doc;
                            break;
                        }
                    }
                }

                inVmMirror = true;
                try
                {
                    if (known is null)
                    {
                        try
                        {
                            await openDocs.OpenAsync(path).ConfigureAwait(true);
                        }
                        catch (System.IO.IOException)
                        {
                            // File became unreadable between VM load and service mirror.
                        }

                        // While this save-mirror was parked on the file read, a
                        // Ctrl+N may have installed a session-only untitled that now
                        // owns the window. OpenAsync's own activation is stale in
                        // that case — re-clear so no file tab stays highlighted over
                        // the untitled editor (suppressed via the latch held here).
                        if (UntitledSessionOwnsWindow())
                        {
                            openDocs.ClearActive();
                        }
                    }
                    else if (!ReferenceEquals(known, openDocs.ActiveDocument)
                        && (pendingDirtySwitchTarget is null
                            || ReferenceEquals(known, pendingDirtySwitchTarget)))
                    {
                        // While a dirty-prompted tab switch is pending, a Save
                        // resolution publishes the OLD document (ApplySavedDocument)
                        // before the queued switch runs; re-activating it here
                        // would flip the tab strip back and split-brain tabs vs
                        // editor. Only the pending target may activate.
                        openDocs.Activate(known);
                    }

                    if (known is not null
                        && !string.Equals(known.SourceText, content, System.StringComparison.Ordinal))
                    {
                        openDocs.UpdateSourceText(known, content);
                    }
                }
                finally
                {
                    inVmMirror = false;
                }
            });
        };

        // Persistence: restore the open documents list saved from the last
        // session, then layer any argv-opened document on top. While the
        // restore loop runs we suppress the auto-save subscription (below)
        // so the saved file isn't rewritten with each intermediate Add.
        var sessionStore = App.Services?.GetService<IApplicateSessionStore>();
        var isRestoring = sessionStore is not null;

        void SaveSession()
        {
            if (isRestoring || sessionStore is null)
            {
                return;
            }

            var openPaths = openDocs.OpenDocuments.Select(d => d.FilePath).ToList();
            // ActivePath must ALWAYS be an OPEN path (or null). Guard BOTH inputs:
            //  - ActiveDocument may briefly still point at a just-removed file
            //    (Close removes the doc from OpenDocuments — firing this
            //    CollectionChanged save — BEFORE it re-points ActiveDocument);
            //  - the last-active-file fallback restores the last-viewed file after a
            //    session-only untitled owns the window (Ctrl+N leaves the file tabs
            //    open), but must not resurrect a file that was closed.
            // A dangling ActivePath with empty OpenPaths would make the next startup
            // hold the reveal gate for a doc that never restores (15s fallback).
            string? PathIfStillOpen(string? path)
                => path is not null && openPaths.Contains(path, System.StringComparer.OrdinalIgnoreCase)
                    ? path
                    : null;
            var snapshot = new ApplicateSession
            {
                OpenPaths = openPaths,
                ActivePath = PathIfStillOpen(openDocs.ActiveDocument?.FilePath)
                    ?? PathIfStillOpen(lastActivePath),
            };
            _ = sessionStore.SaveAsync(snapshot).AsTask();
        }

        ((INotifyCollectionChanged)openDocs.OpenDocuments).CollectionChanged += (_, _) => SaveSession();
        openDocs.ActiveDocumentChanged += (_, _) =>
        {
            if (openDocs.ActiveDocument is not null)
            {
                lastActivePath = openDocs.ActiveDocument.FilePath;
            }
            SaveSession();
        };

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            ApplicateSession saved = ApplicateSession.Empty;
            if (sessionStore is not null)
            {
                try
                {
                    saved = await sessionStore.LoadAsync().ConfigureAwait(true);
                }
                catch
                {
                    saved = ApplicateSession.Empty;
                }
            }

            var argvPath = App.Services?.GetService<ICommandLineActivation>()?.GetActivationFilePath();
            if (string.IsNullOrWhiteSpace(argvPath))
            {
                argvPath = viewModel.Document?.Path;
            }

            // Multi-tab startup-scaling polish: determine the preferred
            // active path UP FRONT so the restore loop knows which one
            // single tab needs a full open (file read) and which ones
            // can be added as lightweight stubs. Argv wins over saved
            // active path because the user just explicitly asked for it
            // (mirrors the existing toActivate priority further below).
            // When no explicit ActivePath exists (legacy session), fall back
            // to the first non-empty open path as the startup document. That
            // keeps cold startup visible-first instead of fully opening every
            // restored tab before the user sees anything.
            var preferredActivePath = !string.IsNullOrWhiteSpace(argvPath)
                ? argvPath
                : saved.GetStartupDocumentPath();
            var canUseStubs = !string.IsNullOrWhiteSpace(preferredActivePath);

            // Open saved paths: the preferred-active path goes through
            // the full OpenAsync so its contents are read into the
            // OpenDocument right here. Non-active paths go through
            // OpenStubAsync (no file read) so cold-startup time does
            // not scale with N tabs. The early-document cache filled
            // by Program.StartSessionStartupDocumentPreRead is the
            // rendezvous for the active-doc fast path; stubs load when the
            // user clicks them later (EnsureLoadedAsync).
            inVmMirror = true;
            try
            {
                foreach (var path in saved.OpenPaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var isPreferred = canUseStubs
                        && string.Equals(
                            path,
                            preferredActivePath,
                            System.StringComparison.OrdinalIgnoreCase);

                    try
                    {
                        if (canUseStubs && !isPreferred)
                        {
                            await openDocs.OpenStubAsync(path).ConfigureAwait(true);
                        }
                        else
                        {
                            await openDocs.OpenAsync(path, activate: false).ConfigureAwait(true);
                        }
                    }
                    catch (System.IO.IOException)
                    {
                        // File may have moved or been deleted since last session.
                    }
                    catch (System.UnauthorizedAccessException)
                    {
                        // Access lost since last session; skip silently.
                    }
                }

                if (!string.IsNullOrWhiteSpace(argvPath))
                {
                    try
                    {
                        await openDocs.OpenAsync(argvPath, activate: false).ConfigureAwait(true);
                    }
                    catch (System.IO.IOException)
                    {
                        // Argv file may have moved between argv parse and now.
                    }
                }
            }
            finally
            {
                inVmMirror = false;
                isRestoring = false;
            }

            // Pick the document to activate. Argv wins over the saved active
            // because the user just explicitly asked for it. If the preferred
            // path no longer exists in the restored set (file deleted, argv
            // pointed at a missing file, etc.) fall back to the first open
            // doc so the user is never left with an "active tab does not
            // match displayed file" state.
            // (preferredActivePath was already computed above to drive the
            // stub-vs-full-open decision in the restore loop; reused here
            // so the activation target stays consistent with the loop's
            // "full open" choice.)
            OpenDocument? toActivate = null;
            if (!string.IsNullOrWhiteSpace(preferredActivePath))
            {
                foreach (var doc in openDocs.OpenDocuments)
                {
                    if (string.Equals(doc.FilePath, preferredActivePath, System.StringComparison.OrdinalIgnoreCase))
                    {
                        toActivate = doc;
                        break;
                    }
                }
            }
            if (toActivate is null && openDocs.OpenDocuments.Count > 0)
            {
                toActivate = openDocs.OpenDocuments[0];
            }

            if (toActivate is not null)
            {
                // Multi-tab startup-scaling polish: make sure the
                // chosen-active OpenDocument is fully loaded before
                // anything reads its SourceText. In the happy path the
                // restore loop above already called the full OpenAsync
                // for the preferred path, so this is a no-op. In the
                // fallback path (preferredActivePath was empty, missing,
                // or refused to load and we picked OpenDocuments[0]
                // which is a stub) the cache hit from
                // Program.StartSessionStartupDocumentPreRead pays for the
                // startup-tab read; on a true cache miss EnsureLoadedAsync
                // falls through to disk. The Activate below fires
                // ActiveDocumentChanged with inVmMirror=true so the
                // bridge handler does not re-EnsureLoadedAsync on top
                // of this call.
                if (!toActivate.IsLoaded)
                {
                    try
                    {
                        await openDocs.EnsureLoadedAsync(toActivate).ConfigureAwait(true);
                    }
                    catch (System.IO.IOException)
                    {
                        // Fall through; the OpenPathAsync below will
                        // surface the typed-error to the VM.
                    }
                    catch (System.UnauthorizedAccessException)
                    {
                        // Same fallthrough as IOException.
                    }
                }

                // Single canonical Activate — no ReferenceEquals dance because
                // the restore loop above intentionally left ActiveDocument
                // unchanged (likely null, unless upstream's argv-load fired
                // PropertyChanged on Document before this lambda ran and the
                // bridge's mirror set it to argvPath's OpenDocument). Either
                // way, an explicit Activate here is correct: either it
                // promotes from null to toActivate, or it confirms toActivate
                // (which the existing SetActive-no-op guard handles silently).
                inVmMirror = true;
                try
                {
                    openDocs.Activate(toActivate);
                }
                finally
                {
                    inVmMirror = false;
                }

                var startupLoadIsPending = viewModel.IsOpeningPath(toActivate.FilePath);
                if (!string.Equals(viewModel.Document?.Path, toActivate.FilePath, System.StringComparison.OrdinalIgnoreCase)
                    && !startupLoadIsPending)
                {
                    inServiceLoad = true;
                    try
                    {
                        await viewModel.OpenPathAsync(toActivate.FilePath).ConfigureAwait(true);
                    }
                    catch (System.IO.IOException)
                    {
                        // File may have moved between restore and the VM load.
                    }
                    finally
                    {
                        inServiceLoad = false;
                    }
                }
            }

            // Flush a consolidated save now that the restored set is final.
            SaveSession();
        });
    }

    // PE r2 item A — pre-warm wiring. Runs once at ctor time, right after
    // InstallSharedWebViewWarmupPanel populated _warmupParent and parented the
    // shared View under it. The visible reader host gets first claim on cold
    // WebView2 startup; edit-preview prewarm is deferred until the reader has
    // revealed or the fallback timer fires. That keeps startup scoped to the
    // visible surface before background work warms the first Ctrl+E path.
    private void InstallSharedWebViewPreWarm()
    {
        var provider = App.Services?.GetService<IApplicateSharedWebViewHostProvider>();
        if (provider is not null)
        {
            _ = provider.ViewerHost.PreWarmShellAsync();
            if (!ReferenceEquals(provider.ViewerHost, provider.EditPreviewHost))
            {
                InstallDeferredSecondaryWebViewPreWarm(provider.ViewerHost, provider.EditPreviewHost);
            }

            return;
        }

        var sharedHost = App.Services?.GetService<IApplicateSharedWebViewHost>();
        if (sharedHost is not null)
        {
            _ = sharedHost.PreWarmShellAsync();
        }
    }

    private void InstallDeferredSecondaryWebViewPreWarm(
        IApplicateSharedWebViewHost visibleHost,
        IApplicateSharedWebViewHost secondaryHost)
    {
        var queued = false;
        DispatcherTimer? delayTimer = null;
        var fallbackTimer = new DispatcherTimer { Interval = SecondaryWebViewPreWarmFallbackDelay };
        EventHandler? onPrimaryDocumentRevealReady = null;
        EventHandler? onPrimaryProgressiveAppendCompleted = null;
        EventHandler<ApplicateRendererFailureEvent>? onPrimaryRendererFailed = null;
        EventHandler? onWindowClosed = null;

        void CleanupTriggers()
        {
            if (onPrimaryDocumentRevealReady is not null)
            {
                visibleHost.View.DocumentRevealReady -= onPrimaryDocumentRevealReady;
            }

            if (onPrimaryProgressiveAppendCompleted is not null)
            {
                visibleHost.View.ProgressiveAppendCompleted -= onPrimaryProgressiveAppendCompleted;
            }

            if (onPrimaryRendererFailed is not null)
            {
                visibleHost.RendererFailed -= onPrimaryRendererFailed;
            }

            fallbackTimer.Stop();
            fallbackTimer.Tick -= OnFallbackTick;
        }

        void CleanupAll()
        {
            CleanupTriggers();
            if (delayTimer is not null)
            {
                delayTimer.Stop();
                delayTimer.Tick -= OnDelayTick;
                delayTimer = null;
            }

            if (onWindowClosed is not null)
            {
                Closed -= onWindowClosed;
            }
        }

        void QueueSecondaryPreWarm(string reason)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(
                    () => QueueSecondaryPreWarm(reason),
                    DispatcherPriority.Background);
                return;
            }

            if (queued)
            {
                return;
            }

            queued = true;
            CleanupTriggers();
            ApplicateTrace.DiagMs(
                "startup-webview",
                "secondary-shell-prewarm-deferred",
                $"reason={reason} delayMs={SecondaryWebViewPreWarmDelay.TotalMilliseconds:F0}");

            delayTimer = new DispatcherTimer { Interval = SecondaryWebViewPreWarmDelay };
            delayTimer.Tick += OnDelayTick;
            delayTimer.Start();
        }

        void QueueSecondaryPreWarmWhenVisibleWorkComplete(string reason)
        {
            if (visibleHost.View.HasPendingProgressiveAppend)
            {
                ApplicateTrace.DiagMs(
                    "startup-webview",
                    "secondary-shell-prewarm-wait-progressive");
                return;
            }

            QueueSecondaryPreWarm(reason);
        }

        void OnDelayTick(object? sender, EventArgs e)
        {
            if (delayTimer is not null)
            {
                delayTimer.Stop();
                delayTimer.Tick -= OnDelayTick;
                delayTimer = null;
            }

            if (onWindowClosed is not null)
            {
                Closed -= onWindowClosed;
                onWindowClosed = null;
            }

            ApplicateTrace.DiagMs("startup-webview", "secondary-shell-prewarm-start");
            InstallWarmupPanelForHost(secondaryHost, index: 1);
            _ = secondaryHost.PreWarmShellAsync();
        }

        void OnFallbackTick(object? sender, EventArgs e)
            => QueueSecondaryPreWarmWhenVisibleWorkComplete("fallback");

        onPrimaryDocumentRevealReady = (_, _) => QueueSecondaryPreWarmWhenVisibleWorkComplete("visible-document-reveal-ready");
        onPrimaryProgressiveAppendCompleted = (_, _) => QueueSecondaryPreWarm("visible-progressive-append-ready");
        onPrimaryRendererFailed = (_, _) => QueueSecondaryPreWarm("visible-renderer-failed");
        onWindowClosed = (_, _) => CleanupAll();

        visibleHost.View.DocumentRevealReady += onPrimaryDocumentRevealReady;
        visibleHost.View.ProgressiveAppendCompleted += onPrimaryProgressiveAppendCompleted;
        visibleHost.RendererFailed += onPrimaryRendererFailed;
        Closed += onWindowClosed;
        fallbackTimer.Tick += OnFallbackTick;
        fallbackTimer.Start();
    }

    private void InstallSharedWebViewWarmupPanel()
    {
        var provider = App.Services?.GetService<IApplicateSharedWebViewHostProvider>();
        if (provider is not null)
        {
            InstallWarmupPanelForHost(provider.ViewerHost, index: 0);
            return;
        }

        var sharedHost = App.Services?.GetService<IApplicateSharedWebViewHost>();
        if (sharedHost is not null)
        {
            InstallWarmupPanelForHost(sharedHost, index: 0);
        }
    }

    private void InstallWarmupPanelForHost(IApplicateSharedWebViewHost sharedHost, int index)
    {
        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        if (bodyPanel is null)
        {
            return;
        }

        var warmupPanel = new Panel
        {
            Width = WarmupPanelWidth,
            Height = WarmupPanelHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(
                WarmupPanelMargin.Left - (WarmupPanelWidth + 32) * index,
                WarmupPanelMargin.Top,
                WarmupPanelMargin.Right,
                WarmupPanelMargin.Bottom),
            IsHitTestVisible = false,
            UseLayoutRounding = true
        };

        bodyPanel.Children.Add(warmupPanel);
        sharedHost.SetWarmupParent(warmupPanel);
    }
}
