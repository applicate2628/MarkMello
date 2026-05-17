using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MarkMello.Application.Abstractions;
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

    private Panel? _tabsContentPanel;
    private ApplicateSiblingMountBridge? _siblingMountBridge;

    public ApplicateMainWindow(
        MainWindowViewModel viewModel,
        StartupSmokeTestOptions startupSmokeTestOptions,
        ISettingsStore settings)
        : base(viewModel, startupSmokeTestOptions, settings)
    {
        var viewerTemplate = new ApplicateViewerTemplate();
        DataTemplates.Insert(0, viewerTemplate);
        InstallViewerHostTemplate(viewerTemplate);
        InstallSharedWebViewWarmupPanel();
        InstallSharedHostPrewarm(viewModel);
        InstallTabsAndWelcome();
        InstallSiblingMountedViews(viewModel);
        InstallHostShortcutBridge(viewModel);
        InstallActiveDocumentBridge(viewModel);
        InstallPopupZOrderFollow(viewModel);
        InstallApplicateAboutPanel();
        InstallPopupFadeIn();
        InstallEditModeDragSuppression(viewModel);
        InstallNativeRendererStub(viewModel);
        Opened += (_, _) => Title = $"{Title} [Applicate overlay]";
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            InstallStatusHintAboveWebView,
            Avalonia.Threading.DispatcherPriority.Loaded);
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

    // ===========================================================
    // TEMP-NATIVE-STUB: temporary stub for the native (Avalonia) markdown
    // renderer until the v0.3 cross-fade-with-screenshot work lands.
    // Native render flashes between Avalonia and WebView during source
    // change, producing visible jitter in edit mode that the v0.2
    // WebView opacity-fade fix does not cover. While the stub is in
    // place: every selection is forced to WebView, and the Renderer
    // row in the reading-settings popup shows a static note instead
    // of the segmented Native/WebView toggle.
    //
    // To restore native renderer support:
    //   1. Remove the InstallNativeRendererStub call in the constructor.
    //   2. Delete this method and DisableNativeToggleInSettings.
    //   3. Verify upstream segmented control rebinds correctly (the
    //      runtime patch here mutates the popup tree at first open).
    // ===========================================================
    private void InstallNativeRendererStub(MainWindowViewModel viewModel)
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

    private void InstallPopupFadeIn()
    {
        // Smooth open transition for the named popup overlays. Avalonia's
        // Popup pops a PopupRoot window instantly, so we instead animate
        // the popup's Child opacity from 0 to 1 on each Opened event. The
        // Transitions collection is installed once per popup; the fade is
        // triggered by setting Opacity = 1 on a dispatch-back-to-UI tick
        // so Avalonia detects a property change to animate over.
        string[] popupNames = ["AppMenuPanel", "AppSettingsPanel", "AppAboutPanel", "SettingsPanel"];
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

    private void InstallApplicateAboutPanel()
    {
        // The upstream AppAboutPanel popup lives in upstream MainWindow.axaml
        // and binds `<views:AppAboutPanelView />` directly. The fork-overlay
        // rule forbids editing upstream files, so we swap the popup's Child
        // here for our subclass that appends the fork credit row. The popup's
        // DataContext (MainWindowViewModel) flows to the new Child via the
        // visual tree, so existing bindings such as AboutVersion keep working.
        var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("AppAboutPanel");
        if (popup is null)
        {
            return;
        }
        popup.Child = new ApplicateAppAboutPanelView();
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

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        Grid.SetRow(tabsView, 0);
        Grid.SetRow(contentPanel, 1);
        grid.Children.Add(tabsView);
        grid.Children.Add(contentPanel);

        bodyPanel.Children.Add(grid);
    }

    private void InstallSiblingMountedViews(MainWindowViewModel viewModel)
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
        var sharedHost = App.Services?.GetService<IApplicateSharedWebViewHost>();
        var editPreview = new ApplicateEditPreviewView(sharedHost);
        var editWorkspace = new EditWorkspaceView
        {
            DataContext = null
        };
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
                parentBorder.Child = editPreview;
            }
        }

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
        // Force template + measure + arrange synchronously while editSlot is
        // briefly IsVisible=true. This realizes the EditWorkspaceView's
        // templated content tree (which UserControl + ContentPresenter
        // otherwise defers until first visible measure pass), fires
        // OnAttachedToVisualTree on EditPreview, and triggers the one-time
        // SharedHost.AttachTo reparent — all while no render frame is
        // produced (Avalonia batches invalidations until next dispatcher
        // tick, so the brief IsVisible=true does not flash on screen).
        editSlot.IsVisible = true;
        editSlot.ApplyTemplate();
        editWorkspace.ApplyTemplate();
        editPreview.ApplyTemplate();
        editSlot.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
        editSlot.Arrange(new Avalonia.Rect(0, 0, editSlot.DesiredSize.Width, editSlot.DesiredSize.Height));
        editSlot.IsVisible = false;

        _siblingMountBridge = new ApplicateSiblingMountBridge(
            viewModel,
            viewerSlot,
            editSlot,
            editWorkspace,
            () => viewModel.IsViewer,
            () => viewModel.IsEditMode,
            () => viewModel.EditorSession,
            () => viewModel.Document,
            viewerContent: viewModel);

        Closed += OnApplicateMainWindowClosed;
    }

    private void OnApplicateMainWindowClosed(object? sender, EventArgs e)
    {
        _siblingMountBridge?.Dispose();
        _siblingMountBridge = null;
        ApplicateWebMarkdownDocumentView.HostShortcutHandler = null;
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

    private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)
    {
        var openDocs = App.Services?.GetService<IOpenDocumentsService>();
        if (openDocs is null)
        {
            return;
        }

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
                    if (viewModel.CloseFileCommand.CanExecute(null))
                    {
                        viewModel.CloseFileCommand.Execute(null);
                    }
                    return;
                }

                var newPath = args.ActiveDocument.FilePath;
                var currentPath = viewModel.Document?.Path;
                if (string.Equals(currentPath, newPath, System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
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
                    var nextSource = new MarkdownSource(
                        args.ActiveDocument.FilePath,
                        args.ActiveDocument.DisplayName,
                        args.ActiveDocument.SourceText);
                    inServiceLoad = true;
                    try
                    {
                        viewModel.ApplyOpenedDocumentInPlace(nextSource);
                    }
                    finally
                    {
                        inServiceLoad = false;
                    }
                    return;
                }

                // Reader-mode tab switch: full reload, preserve edit flag
                // is a defensive no-op here because we already gated above.
                var wasEditMode = viewModel.IsEditMode;
                inServiceLoad = true;
                try
                {
                    await viewModel.OpenPathAsync(newPath).ConfigureAwait(true);
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
            if (args.PropertyName != nameof(MainWindowViewModel.Document))
            {
                return;
            }
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
                    }
                    else if (!ReferenceEquals(known, openDocs.ActiveDocument))
                    {
                        openDocs.Activate(known);
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

            var snapshot = new ApplicateSession
            {
                OpenPaths = openDocs.OpenDocuments.Select(d => d.FilePath).ToList(),
                ActivePath = openDocs.ActiveDocument?.FilePath,
            };
            _ = sessionStore.SaveAsync(snapshot).AsTask();
        }

        ((INotifyCollectionChanged)openDocs.OpenDocuments).CollectionChanged += (_, _) => SaveSession();
        openDocs.ActiveDocumentChanged += (_, _) => SaveSession();

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

            var argvPath = viewModel.Document?.Path;

            // Open all saved + argv paths WITHOUT auto-activating each. The
            // service's OpenAsync(activate: false) overload skips the
            // SetActive side-effect so the loop does not bounce
            // ActiveDocument between every restored file. After the loop,
            // we Activate the chosen one exactly once. This eliminates the
            // earlier "v0.2.x костыль" force-sync race where ActiveDocument
            // ended up at whatever was opened last and the tabs UI drifted
            // out of sync with VM.Document on subsequent activations.
            inVmMirror = true;
            try
            {
                foreach (var path in saved.OpenPaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }
                    try
                    {
                        await openDocs.OpenAsync(path, activate: false).ConfigureAwait(true);
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
            var preferredPath = !string.IsNullOrWhiteSpace(argvPath) ? argvPath : saved.ActivePath;
            OpenDocument? toActivate = null;
            if (!string.IsNullOrWhiteSpace(preferredPath))
            {
                foreach (var doc in openDocs.OpenDocuments)
                {
                    if (string.Equals(doc.FilePath, preferredPath, System.StringComparison.OrdinalIgnoreCase))
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

                if (!string.Equals(viewModel.Document?.Path, toActivate.FilePath, System.StringComparison.OrdinalIgnoreCase))
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

    // Push the active document into the singleton shared WebView while it is
    // still parented to the offscreen warmup panel — so the renderer's heavy
    // first paint (~530ms RenderAsync + shell-load + JS messaging) happens
    // before the user ever presses Ctrl+E. Without this, the first enter-edit
    // pays the full WebView render synchronously inside the toggle window
    // (verified at 532ms in .scratch/mode-toggle.log 07:40:12.771→13.303).
    // Subsequent UpdateInputs from EditPreviewView.ApplyWebPreviewSource hit
    // a hot WebView with Source/ImageSourceResolver already matching → action
    // degrades to None/ApplyLivePreferences, no full re-render.
    private void InstallSharedHostPrewarm(MainWindowViewModel viewModel)
    {
        var sharedHost = App.Services?.GetService<IApplicateSharedWebViewHost>();
        if (sharedHost is null)
        {
            return;
        }

        var pumpScheduled = false;

        void Push()
        {
            pumpScheduled = false;
            var doc = viewModel.Document;
            if (doc is null)
            {
                return;
            }

            try
            {
                var prefs = ApplicateEditPreviewView.CreateWebPreviewPreferences(viewModel.DocumentReadingPreferences);
                var width = ApplicateEditPreviewView.CalculatePreWarmColumnWidth(viewModel.DocumentReadingPreferences);
                sharedHost.View.UpdateInputs(
                    source: doc,
                    readingPreferences: prefs,
                    imageSourceResolver: viewModel.ImageSourceResolver,
                    availableContentWidth: width,
                    viewerChromeEnabled: false,
                    documentScrollEnabled: true,
                    wheelProxyEnabled: false);
                System.Console.Error.WriteLine(
                    $"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost prewarm pushed: path={doc.Path} width={width:F1}");
            }
            catch (System.Exception ex)
            {
                System.Console.Error.WriteLine(
                    $"[mode-toggle] SharedHost prewarm FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        void Schedule()
        {
            if (pumpScheduled)
            {
                return;
            }
            pumpScheduled = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Push,
                Avalonia.Threading.DispatcherPriority.Background);
        }

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Document)
                || e.PropertyName == nameof(MainWindowViewModel.DocumentReadingPreferences)
                || e.PropertyName == nameof(MainWindowViewModel.ImageSourceResolver))
            {
                Schedule();
            }
        };

        // Initial push covers command-line activation, restore-session,
        // and any synchronous load completed before the constructor finished.
        Schedule();
    }

    private void InstallSharedWebViewWarmupPanel()
    {
        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        var sharedHost = App.Services?.GetService<IApplicateSharedWebViewHost>();
        if (bodyPanel is null || sharedHost is null)
        {
            return;
        }

        var warmupPanel = new Panel
        {
            Width = WarmupPanelWidth,
            Height = WarmupPanelHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = WarmupPanelMargin,
            IsHitTestVisible = false,
            UseLayoutRounding = true
        };

        bodyPanel.Children.Add(warmupPanel);
        sharedHost.SetWarmupParent(warmupPanel);
    }
}
