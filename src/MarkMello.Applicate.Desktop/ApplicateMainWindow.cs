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

    public ApplicateMainWindow(
        MainWindowViewModel viewModel,
        StartupSmokeTestOptions startupSmokeTestOptions,
        ISettingsStore settings)
        : base(viewModel, startupSmokeTestOptions, settings)
    {
        var viewerTemplate = new ApplicateViewerTemplate();
        var editWorkspaceTemplate = new ApplicateEditWorkspaceTemplate();
        DataTemplates.Insert(0, editWorkspaceTemplate);
        DataTemplates.Insert(0, viewerTemplate);
        InstallViewerHostTemplate(viewerTemplate);
        InstallSharedWebViewWarmupPanel();
        InstallTabsAndWelcome();
        InstallActiveDocumentBridge(viewModel);
        InstallPopupZOrderFollow(viewModel);
        InstallApplicateAboutPanel();
        InstallPopupFadeIn();
        InstallEditModeDragSuppression(viewModel);
        InstallNativeRendererStub(viewModel);
        Opened += (_, _) => Title = $"{Title} [Applicate overlay]";
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

        // Hide the Native toggle row inside the ReadingSettings popup.
        // The popup materializes its content lazily on first open and
        // Avalonia may re-attach the Child instance on subsequent opens,
        // so subscribe to BOTH Opened (covers first open) and the popup
        // child's Loaded (covers attach lifecycle) and re-apply on each.
        var settingsPopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("SettingsPanel");
        if (settingsPopup is not null)
        {
            settingsPopup.Opened += DisableNativeToggleInSettings;
            if (settingsPopup.Child is Control settingsChild)
            {
                settingsChild.Loaded += (_, _) => DisableNativeToggleInSettings(settingsPopup, EventArgs.Empty);
            }
        }
    }

    private static void DisableNativeToggleInSettings(object? sender, System.EventArgs e)
    {
        if (sender is not Avalonia.Controls.Primitives.Popup popup || popup.Child is null)
        {
            return;
        }
        // Use Render priority so we run AFTER Avalonia has finished
        // measuring/arranging the popup's contents on each open. Background
        // priority sometimes ran before the segmented control was fully
        // materialized, so the lookup found zero ToggleButtons and the
        // patch silently no-oped — the user saw the row come back.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // The segmented Native/WebView toggles in ReadingSettingsPanelView
            // sit inside a Border.mm-segmented containing two ToggleButton
            // children: Native is declared first (axaml line 263), WebView
            // second (line 267). Walk the popup tree, find that pair, and
            // disable the first one. Restated every popup open in case
            // Avalonia rebuilds the popup content lazily.
            var segmented = popup.Child.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Where(tb => tb.Classes.Contains("mm-segmented-item"))
                .Take(2)
                .ToList();
            if (segmented.Count == 0)
            {
                // Tree not yet materialized — schedule one more tick with
                // longer delay. Avoids the silent-noop case where Render
                // priority still ran ahead of the segmented control's
                // first layout pass.
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => DisableNativeToggleInSettings(popup, System.EventArgs.Empty),
                    Avalonia.Threading.DispatcherPriority.Background);
                return;
            }
            // TEMP-NATIVE-STUB: replace the Native/WebView segmented toggle
            // with a static label. Walk up the visual ancestors directly
            // (`GetVisualAncestors().OfType<Border>().FirstOrDefault`
            // missed the segmented border in some popup-open timings,
            // possibly because the wrapper hierarchy includes additional
            // Decorator nodes). Direct parent walk: ToggleButton -> inner
            // StackPanel -> mm-segmented Border -> row Grid.
            var nativeToggle = segmented[0];
            var stackPanel = nativeToggle.Parent as Control;
            var segmentedBorder = stackPanel?.Parent as Border;
            var rowGrid = segmentedBorder?.Parent as Grid;
            if (segmentedBorder is not null && rowGrid is not null)
            {
                segmentedBorder.IsVisible = false;
                if (!rowGrid.Children.OfType<TextBlock>()
                        .Any(t => t.Tag as string == "applicate-renderer-note"))
                {
                    var note = new TextBlock
                    {
                        Classes = { "mm-setting-meta" },
                        Tag = "applicate-renderer-note",
                        Text = "WebView (расширенный рендер)",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };
                    Grid.SetColumn(note, Grid.GetColumn(segmentedBorder));
                    rowGrid.Children.Add(note);
                }
            }
            else
            {
                nativeToggle.IsVisible = false;
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
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
                // reader mode before we re-toggle back to edit. Instead
                // update Document + EditorSession directly with the
                // service's cached source; edit-workspace UI stays mounted
                // throughout. Reader-mode path keeps using OpenPathAsync
                // because reader-mode preview reads RenderedDocument which
                // OpenPathAsync refreshes.
                if (viewModel.IsEditMode && viewModel.EditorSession is { } session)
                {
                    var nextSource = new MarkdownSource(
                        args.ActiveDocument.FilePath,
                        args.ActiveDocument.DisplayName,
                        args.ActiveDocument.SourceText);
                    inServiceLoad = true;
                    try
                    {
                        viewModel.Document = nextSource;
                        session.ApplyLoadedDocument(nextSource);
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
                        await openDocs.OpenAsync(path).ConfigureAwait(true);
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
                        await openDocs.OpenAsync(argvPath).ConfigureAwait(true);
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
                // Force-sync both sides. The restore loop ran under
                // inVmMirror=true so every OpenAsync's ActiveDocumentChanged
                // was dropped by the bridge — the service's ActiveDocument
                // ended up at whichever doc was opened last (= last entry in
                // saved.OpenPaths, or argvPath if supplied), and the VM was
                // never told to load any of them. Either side may now be
                // out of sync with `toActivate`; touch both explicitly so
                // they cannot drift.
                if (!ReferenceEquals(toActivate, openDocs.ActiveDocument))
                {
                    // Suppress the bridge while we set ActiveDocument so its
                    // async OpenPathAsync does not race with our own
                    // explicit OpenPathAsync below.
                    inVmMirror = true;
                    try
                    {
                        openDocs.Activate(toActivate);
                    }
                    finally
                    {
                        inVmMirror = false;
                    }
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
