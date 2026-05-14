using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
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
        Opened += (_, _) => Title = $"{Title} [Applicate overlay]";
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

                // Preserve edit mode across tab switches: upstream
                // OpenPathAsync hard-resets to viewer; we re-toggle into
                // edit afterwards when that was the prior state.
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
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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
            // because the user just explicitly asked for it.
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

            if (toActivate is not null && !ReferenceEquals(toActivate, openDocs.ActiveDocument))
            {
                openDocs.Activate(toActivate);
            }
            else if (toActivate is not null
                && string.IsNullOrEmpty(viewModel.Document?.Path))
            {
                // Restore opened the docs under inVmMirror, so the bridge's
                // ActiveDocumentChanged handler skipped each Add and the VM
                // was never told what document to load. If the preferred doc
                // is already the active OpenDocument (set as a side effect of
                // the last OpenAsync) the Activate() above is a no-op, so we
                // must explicitly drive the VM to that path here.
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
