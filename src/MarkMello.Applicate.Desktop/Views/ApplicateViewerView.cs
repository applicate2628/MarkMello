using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Read-only viewer surface for the active document. WebView2 is the only
/// renderer; failure routes through <see cref="ApplicateRendererFailureView"/>.
///
/// Phase 4 collapses this control to a passive slot owner that delegates the
/// rendered surface to <see cref="IApplicateSharedWebViewHost"/>. The host
/// reparents the shared WebView into <c>_webSlot</c> when the viewer is
/// visible, owns the slot's <c>IsVisible</c> across SWITCHING ↔ COMMITTED
/// transitions, and surfaces runtime / per-document failures through
/// <see cref="IApplicateSharedWebViewHost.RendererFailed"/>.
/// </summary>
public sealed class ApplicateViewerView : UserControl, IDisposable
{
    private const double WheelStepMultiplier = 6.0;
    /// <summary>
    /// Backwards-compatible re-export of
    /// <see cref="ApplicateDocumentLayout.MinManualContentWidth"/>. The
    /// canonical owner is <see cref="ApplicateDocumentLayout"/>; this
    /// alias keeps existing test-surface and shared-document-view callers
    /// working without churning their references in this audit pass.
    /// </summary>
    internal const double MinManualContentWidth = ApplicateDocumentLayout.MinManualContentWidth;
    private const double ViewportHorizontalGutter = 32.0;

    // _webSlot owns the airspace the shared WebView lives in while the
    // viewer is the active consumer. The shared host flips _webSlot.IsVisible
    // between SWITCHING (false) and COMMITTED (true). The right margin
    // reserves room for the Avalonia ScrollBar overlay (mounted as a sibling
    // by the shared-host wiring above) so the WebView2 HWND does not paint
    // into the scrollbar strip via Win32 z-order. Width comes from
    // ApplicateDocumentLayout.GetWebSlotScrollBarGutter which reads the
    // canonical ScrollBarSize theme resource — same value that drives the
    // ScrollBar style's painted width, so reservation and bar agree.
    private readonly Panel _webSlot = new()
    {
        UseLayoutRounding = true,
        Margin = ApplicateDocumentLayout.GetWebSlotScrollBarGutter(),
    };
    private readonly ScrollViewer _scroll;
    private readonly Border _scrollContentFrame;
    private readonly Border _column;
    private readonly Grid _documentShell;
    private readonly Grid _documentLayer;
    private readonly IApplicateSharedWebViewHost? _sharedHost;
    private readonly ApplicateRendererFailureView _failureView;
    private WebViewHostScrollBarOverlay? _scrollBarOverlay;
    private MainWindowViewModel? _viewModel;
    private bool _isDraggingWidth;
    private double _dragStartWidth;
    private double? _manualContentWidth;
    private double _lastViewModelContentWidth;
    private double _lastReadingProgress;
    // F-04 fix: initialize to 0.0; SyncFromViewModel is the sole writer.
    // The previous 144.0 default was a phantom value mirroring the
    // edit-preview's hardcoded padding sum; it was always overwritten
    // before any render so the literal misled readers about the source
    // of truth (ReadingLayoutMetrics.GetDocumentHorizontalPadding).
    private double _documentHorizontalPadding;
    private double _webMinimapReservedWidth;
    private MarkdownSource? _lastDocumentSource;
    private bool _hostEventsWired;
    private bool _isAttachedToHost;
    private bool _hasValidBounds;

    public ApplicateViewerView(
        IApplicateHtmlMarkdownRenderer? htmlRenderer = null,
        IApplicateShellAssetBundleFactory? shellAssetFactory = null,
        IApplicateSharedWebViewHost? sharedHost = null)
    {
        // htmlRenderer / shellAssetFactory parameters preserved for ctor
        // compatibility with the legacy IDataTemplate; the renderer is now
        // owned exclusively by the shared host. The parameters are silently
        // ignored — keeping them in the signature avoids breaking
        // ApplicateViewerTemplate's three-arg construction call.
        _ = htmlRenderer;
        _ = shellAssetFactory;
        _sharedHost = sharedHost;

        _failureView = new ApplicateRendererFailureView
        {
            IsVisible = false,
        };

        // documentLayer hosts the WebView slot and the optional failure
        // overlay. The Avalonia ScrollBar overlay (mounted once the host
        // attaches the View) is added directly into documentLayer too — see
        // EnsureSharedHostMounted.
        _documentLayer = new Grid { UseLayoutRounding = true };
        _documentLayer.Children.Add(_webSlot);
        _documentLayer.Children.Add(_failureView);

        _documentShell = new Grid { UseLayoutRounding = true };
        _documentShell.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(_documentLayer, 0);
        _documentShell.Children.Add(_documentLayer);

        _column = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            UseLayoutRounding = true,
            Child = _documentShell,
        };

        _scrollContentFrame = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            UseLayoutRounding = true,
            Child = _column,
        };

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // WebView owns its internal scroll, so the outer Avalonia
            // ScrollViewer stays in disabled mode. Kept as a wrapper so the
            // outer host layout (welcome screen, error states) can still
            // place this control without measuring through the WebView2
            // HWND.
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            UseLayoutRounding = true,
            Content = new Grid
            {
                UseLayoutRounding = true,
                Children =
                {
                    _scrollContentFrame,
                },
            },
        };
        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);

        Content = new Grid
        {
            UseLayoutRounding = true,
            Children =
            {
                _scroll,
            },
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachViewModel(DataContext as MainWindowViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Subscribe to ancestor visibility flips so we only AttachTo when
        // the bridge-controlled outer slot actually shows the viewer to the
        // user. The bridge toggles viewerSlot/editSlot IsVisible — we hook
        // both this control's own IsVisible and walk up to find an ancestor
        // ContentControl/Panel that the bridge owns.
        AttachedToVisualTree += OnAnyAttachmentChange;
        DetachedFromVisualTree += OnAnyAttachmentChange;
        PropertyChanged += OnViewerPropertyChanged;
        AttachAncestorVisibilityListeners();
        OnEffectiveVisibilityChanged();
        SyncFromViewModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        AttachedToVisualTree -= OnAnyAttachmentChange;
        DetachedFromVisualTree -= OnAnyAttachmentChange;
        PropertyChanged -= OnViewerPropertyChanged;
        DetachAncestorVisibilityListeners();
        UnwireSharedHostEvents();
        AttachViewModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private readonly System.Collections.Generic.List<Avalonia.Visual> _ancestorListeners = new();

    private void OnAnyAttachmentChange(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachAncestorVisibilityListeners();
        AttachAncestorVisibilityListeners();
        OnEffectiveVisibilityChanged();
    }

    private void AttachAncestorVisibilityListeners()
    {
        for (Avalonia.Visual? v = this; v is not null; v = v.GetVisualParent())
        {
            v.PropertyChanged += OnAncestorPropertyChanged;
            _ancestorListeners.Add(v);
        }
    }

    private void DetachAncestorVisibilityListeners()
    {
        foreach (var v in _ancestorListeners)
        {
            v.PropertyChanged -= OnAncestorPropertyChanged;
        }
        _ancestorListeners.Clear();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            OnEffectiveVisibilityChanged();
        }
    }

    private void OnViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            OnEffectiveVisibilityChanged();
        }
    }

    private void OnEffectiveVisibilityChanged()
    {
        if (IsEffectivelyVisible)
        {
            // Always re-AttachTo on visibility — the host's AttachTo is a
            // no-op when the target panel is already its current parent,
            // and a reparent when not. This handles the edit-preview→viewer
            // mode toggle where edit had stolen the WebView previously.
            EnsureSharedHostMounted(force: true);
            IssueRenderRequest();
        }
        else
        {
            _isAttachedToHost = false;
            // F-05 fix: hand consumer ownership of the scrollbar overlay
            // back to the inactive state when this view stops being the
            // shared WebView's owner.
            if (_scrollBarOverlay is not null)
            {
                _scrollBarOverlay.IsAttachedToHost = false;
            }
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (!_hasValidBounds && Bounds.Width > 0 && Bounds.Height > 0)
        {
            // First measured layout rectangle — release the startup gate and
            // perform a single full sync with real geometry. This replaces the
            // 3 phantom-width renders that previously fired before the layout
            // pass completed.
            _hasValidBounds = true;
            SyncFromViewModel();
            return;
        }

        ApplyColumnWidth();
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ScrollToHeadingRequested -= OnViewModelScrollToHeadingRequested;
            _viewModel.OpenFindBarRequested -= OnViewModelOpenFindBarRequested;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            // v0.3.2 TOC + magnifier — the shell raises these events when
            // the user clicks a TOC row or the magnifier toolbar button.
            // The viewer is the canonical "renderer surface owner" while
            // visible, so we route the IPC call through this view's
            // shared-host reference.
            _viewModel.ScrollToHeadingRequested += OnViewModelScrollToHeadingRequested;
            _viewModel.OpenFindBarRequested += OnViewModelOpenFindBarRequested;
        }

        SyncFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // F-09 fix: subscribe only to canonical preferences source
        // (ReadingPreferences) — the deleted DocumentReadingPreferences
        // ghost copy is no longer published, and SyncFromViewModel always
        // reads ReadingPreferences directly.
        if (e.PropertyName is nameof(MainWindowViewModel.RenderedDocument)
            or nameof(MainWindowViewModel.Document)
            or nameof(MainWindowViewModel.DocumentColumnMaxWidth)
            or nameof(MainWindowViewModel.ReadingPreferences))
        {
            SyncFromViewModel();
        }
    }

    private void SyncFromViewModel()
    {
        if (_viewModel is null)
        {
            _lastDocumentSource = null;
            _manualContentWidth = null;
            _lastViewModelContentWidth = 0;
            _lastReadingProgress = 0;
            _webMinimapReservedWidth = 0;
            _column.MaxWidth = double.PositiveInfinity;
            return;
        }

        var documentChanged = !ReferenceEquals(_lastDocumentSource, _viewModel.Document);
        if (documentChanged)
        {
            _lastDocumentSource = _viewModel.Document;
            _webMinimapReservedWidth = 0;
            _lastReadingProgress = 0;
            // A document switch means any previously displayed failure view
            // is stale. Clear it so the failure overlay does not linger over
            // the new document's first paint.
            _failureView.IsVisible = false;
        }

        var viewModelContentWidth = _viewModel.ContentWidthSetting;
        _documentHorizontalPadding = SysMath.Max(0, _viewModel.DocumentColumnMaxWidth - viewModelContentWidth);
        if (_manualContentWidth is null
            || (!_isDraggingWidth && SysMath.Abs(viewModelContentWidth - _lastViewModelContentWidth) > double.Epsilon))
        {
            _manualContentWidth = viewModelContentWidth;
        }

        _lastViewModelContentWidth = viewModelContentWidth;

        IssueRenderRequest();
        ApplyColumnWidth();
    }

    private void IssueRenderRequest()
    {
        if (_sharedHost is null || _viewModel is null)
        {
            return;
        }

        // Only own the WebView while we are actually visible. The
        // effective-visibility listener calls IssueRenderRequest when this
        // consumer becomes visible; calls outside that window are skipped
        // so we do not steal the WebView from edit-preview.
        if (!_isAttachedToHost || !IsEffectivelyVisible)
        {
            return;
        }

        // Startup gate (see _hasValidBounds field doc): refuse to push render
        // requests until the host's layout rectangle has been measured. Three
        // sync triggers fire before that point at startup; gating here folds
        // them into one render that uses the real width. OnSizeChanged
        // re-triggers SyncFromViewModel on the first valid measurement.
        if (!_hasValidBounds)
        {
            return;
        }

        // Single source of truth for reader policy (minimap mode, font, line
        // height, content width, etc.) is _viewModel.ReadingPreferences — the
        // shell-level value the user sees and edits in the menu. The host
        // gets this canonical record directly. No merge dance, no stripped
        // intermediate (_documentReadingPreferences). The previous indirection
        // through MainWindowViewModel.GetDocumentRenderingPreferences existed
        // to feed the now-removed Avalonia native renderer with a policy-free
        // payload; the WebView has always been the source-of-truth consumer.
        var request = new ApplicateWebRenderRequest(
            ReadingPreferences: _viewModel.ReadingPreferences,
            ImageSourceResolver: _viewModel.ImageSourceResolver,
            AvailableContentWidth: CalculateDocumentColumnWidthForWebSurface());

        _sharedHost.RequestRender(_viewModel.Document, request);
    }

    private void EnsureSharedHostMounted(bool force = false)
    {
        if (_sharedHost is null)
        {
            return;
        }

        if (!force && _isAttachedToHost)
        {
            return;
        }

        var intent = new ApplicateWebMountIntent(
            ViewerChromeEnabled: true,
            DocumentScrollEnabled: true,
            WheelProxyEnabled: true);
        _sharedHost.AttachTo(_webSlot, intent);
        _isAttachedToHost = true;

        // Mount the Avalonia ScrollBar overlay against the shared WebView.
        // Sibling-mounted inside _documentLayer so the WebView2 HWND does
        // not occlude it via Win32 z-order. Disposed in Dispose().
        if (_scrollBarOverlay is null)
        {
            _scrollBarOverlay = new WebViewHostScrollBarOverlay(_sharedHost.View);
            _documentLayer.Children.Add(_scrollBarOverlay.Control);
        }
        // F-05 fix: signal consumer ownership now that the shared WebView
        // has been re-attached to this view's slot.
        _scrollBarOverlay.IsAttachedToHost = true;

        WireSharedHostEvents();
    }

    private void WireSharedHostEvents()
    {
        if (_sharedHost is null || _hostEventsWired)
        {
            return;
        }

        _sharedHost.View.DocumentRendered += OnHostDocumentRendered;
        _sharedHost.View.ScrollStateChanged += OnHostScrollStateChanged;
        _sharedHost.View.MinimapStateChanged += OnHostMinimapStateChanged;
        _sharedHost.View.WidthDragRequested += OnHostWidthDragRequested;
        _sharedHost.View.WheelRequested += OnHostWheelRequested;
        _sharedHost.View.ViewerInteractionRequested += OnHostViewerInteractionRequested;
        // v0.3.2 TOC — heading list and active-heading reports drive the
        // Avalonia-side Table of Contents panel. We forward to the VM only
        // when this view is the active consumer so edit-preview heading
        // events do not overwrite the viewer's TOC contents.
        _sharedHost.View.HeadingsChanged += OnHostHeadingsChanged;
        _sharedHost.View.ActiveHeadingChanged += OnHostActiveHeadingChanged;
        _sharedHost.RendererFailed += OnHostRendererFailed;
        _hostEventsWired = true;
    }

    private void UnwireSharedHostEvents()
    {
        if (_sharedHost is null || !_hostEventsWired)
        {
            return;
        }

        _sharedHost.View.DocumentRendered -= OnHostDocumentRendered;
        _sharedHost.View.ScrollStateChanged -= OnHostScrollStateChanged;
        _sharedHost.View.MinimapStateChanged -= OnHostMinimapStateChanged;
        _sharedHost.View.WidthDragRequested -= OnHostWidthDragRequested;
        _sharedHost.View.WheelRequested -= OnHostWheelRequested;
        _sharedHost.View.ViewerInteractionRequested -= OnHostViewerInteractionRequested;
        _sharedHost.View.HeadingsChanged -= OnHostHeadingsChanged;
        _sharedHost.View.ActiveHeadingChanged -= OnHostActiveHeadingChanged;
        _sharedHost.RendererFailed -= OnHostRendererFailed;
        _hostEventsWired = false;
    }

    private void OnHostHeadingsChanged(object? sender, System.Collections.Generic.IReadOnlyList<MarkMello.Presentation.ViewModels.DocumentHeading> headings)
    {
        // Consumer-side filter — only the active viewer's TOC reflects the
        // current document; edit-preview heading payloads are dropped so
        // the user's TOC list stays anchored on the reading-mode document.
        if (!_isAttachedToHost || _viewModel is null)
        {
            return;
        }
        _viewModel.UpdateDocumentHeadings(headings);
    }

    private void OnHostActiveHeadingChanged(object? sender, string id)
    {
        if (!_isAttachedToHost || _viewModel is null)
        {
            return;
        }
        _viewModel.ActiveHeadingId = id;
    }

    private void OnViewModelScrollToHeadingRequested(object? sender, string id)
    {
        if (_sharedHost is null)
        {
            return;
        }
        _sharedHost.View.ScrollToHeading(id);
    }

    private void OnViewModelOpenFindBarRequested(object? sender, EventArgs e)
    {
        if (_sharedHost is null)
        {
            return;
        }
        _sharedHost.View.OpenFindBar();
    }

    private void OnHostDocumentRendered(object? sender, EventArgs e)
    {
        _viewModel?.MarkReadableDocumentRendered();
        // Any prior failure view dismissed on successful render — the host
        // committed the slot to IsVisible=true, and a stale failure overlay
        // beneath the now-visible WebView would block input.
        _failureView.IsVisible = false;
    }

    private void OnHostRendererFailed(object? sender, ApplicateRendererFailureEvent e)
    {
        // Consumer-side filter: react only when this view is the active host
        // consumer. The host fires RendererFailed once per failure but both
        // consumers (viewer + edit-preview) are subscribed; without this
        // filter the inactive surface would also show the failure overlay.
        // The single source of truth for "active consumer" is _isAttachedToHost.
        if (!_isAttachedToHost)
        {
            return;
        }

        _failureView.ShowFailure(
            e,
            retry: e.Kind == ApplicateRendererFailureKind.DocumentRenderFailed ? RetryCurrentRender : null);
    }

    private void RetryCurrentRender() => _sharedHost?.RetryRender();

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // The WebView owns scroll geometry now; OnHostScrollStateChanged
        // drives reading-progress. The outer Avalonia ScrollViewer is
        // disabled, so this handler is effectively a no-op kept for the
        // ScrollChanged subscription surface.
        if (_viewModel is null)
        {
            return;
        }

        var max = _scroll.ScrollBarMaximum.Y;
        var current = _scroll.Offset.Y;
        if (max > 0)
        {
            _lastReadingProgress = SysMath.Clamp(current / max * 100.0, 0, 100);
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (SysMath.Abs(e.Delta.Y) <= double.Epsilon || SysMath.Abs(e.Delta.X) > SysMath.Abs(e.Delta.Y))
        {
            return;
        }

        var baseStep = _scroll.SmallChange.Height > 0 ? _scroll.SmallChange.Height : 16.0;
        if (ScrollByWheelDelta(-e.Delta.Y * baseStep * WheelStepMultiplier))
        {
            e.Handled = true;
        }
    }

    private bool ScrollByWheelDelta(double deltaY)
    {
        if (_sharedHost is null)
        {
            return false;
        }

        // Outer ScrollViewer is disabled — delegate the scroll-by request to
        // the shared WebView so the renderer's own scroll position responds.
        _sharedHost.View.ScrollDocumentBy(deltaY);
        return true;
    }

    internal static double NormalizeWebWheelDeltaForTesting(double deltaY, int deltaMode, double smallChangeHeight, double viewportHeight)
        => NormalizeWebWheelDelta(deltaY, deltaMode, smallChangeHeight, viewportHeight);

    private static double NormalizeWebWheelDelta(double deltaY, int deltaMode, double smallChangeHeight, double viewportHeight)
    {
        var baseStep = smallChangeHeight > 0 ? smallChangeHeight : 16.0;
        return deltaMode switch
        {
            1 => deltaY * baseStep * 3.0,
            2 => deltaY * SysMath.Max(baseStep, viewportHeight * 0.85),
            _ => deltaY,
        };
    }

    private void FinishWidthHandleDrag()
    {
        _isDraggingWidth = false;
        SetWebDocumentHitTestingForWidthDrag(true);
    }

    private void ApplyWidthDragDelta(double deltaX)
    {
        _manualContentWidth = ClampManualContentWidth(CalculateWidthDragContentWidth(_dragStartWidth, deltaX));
        ApplyColumnWidth();
    }

    private void SetWebDocumentHitTestingForWidthDrag(bool enabled)
    {
        if (_sharedHost is not null)
        {
            _sharedHost.View.IsHitTestVisible = enabled;
        }
    }

    private void ApplyColumnWidth()
    {
        if (_viewModel is null)
        {
            return;
        }

        // Skip column-width math against an unmeasured layout. The clamp
        // floor of 320 + phantom padding produces an apparent width of 464,
        // which is propagated to Chromium via _sharedHost.View.AvailableContentWidth.
        // OnSizeChanged retriggers a full SyncFromViewModel once bounds are
        // real, which calls ApplyColumnWidth again with the correct width.
        if (!_hasValidBounds)
        {
            return;
        }

        var desiredContentWidth = _manualContentWidth ?? _viewModel.ContentWidthSetting;
        var visibleContentWidth = ClampManualContentWidth(desiredContentWidth);
        var documentColumnWidth = visibleContentWidth + _documentHorizontalPadding;

        // Single-source consumer write: only the active consumer writes
        // to the shared ApplicateWebMarkdownDocumentView. Without this
        // guard the inactive viewer (with stale Bounds from its last
        // visible state) kept overwriting AvailableContentWidth on the
        // shared View whenever MainWindowViewModel PropertyChanged
        // events (Document, RenderedDocument, ReadingPreferences)
        // cascaded through OnViewModelPropertyChanged →
        // SyncFromViewModel → ApplyColumnWidth.
        if (_sharedHost is not null && _isAttachedToHost && IsEffectivelyVisible)
        {
            _sharedHost.View.AvailableContentWidth = CalculateDocumentColumnWidthForWebSurface();
        }
        // View.MinHeight intentionally NOT set here. The View has
        // VerticalAlignment=Stretch (set in its ctor) and is parented
        // to _webSlot, so layout naturally arranges it at the slot's
        // allocated height. The previous Max(480, Bounds.Height)
        // assignment was a hardcoded floor that, when the viewer was
        // inactive but PropertyChanged fired through it, leaked viewer-
        // slot-sized MinHeight onto the shared View while edit-preview
        // owned it (edit-preview's slot is shorter by Row 0 toolbar),
        // causing View overflow → HWND paint over toolbar.

        var documentLayerWidth = CalculateDocumentLayerWidth(documentColumnWidth, Bounds.Width, useWebRenderer: true);
        var shellWidth = documentLayerWidth;

        _documentLayer.Width = documentLayerWidth;
        _column.Width = shellWidth;
        _column.MaxWidth = shellWidth;
    }

    internal static double CalculateDocumentLayerWidth(double documentColumnWidth, double hostWidth, bool useWebRenderer)
        => useWebRenderer
            ? SysMath.Max(documentColumnWidth, hostWidth)
            : documentColumnWidth;

    internal static double CalculateWidthDragContentWidth(double dragStartWidth, double deltaX)
        => dragStartWidth + deltaX * 2.0;

    private double ClampManualContentWidth(double contentWidth)
    {
        var availableWidth = CalculateAvailableContentWidth(
            Bounds.Width,
            _webMinimapReservedWidth,
            _documentHorizontalPadding,
            useWebRenderer: true);
        return SysMath.Clamp(contentWidth, MinManualContentWidth, availableWidth);
    }

    private double CalculateDocumentColumnWidthForWebSurface()
    {
        if (_viewModel is null)
        {
            return double.NaN;
        }

        var desiredContentWidth = _manualContentWidth ?? _viewModel.ContentWidthSetting;
        return ClampManualContentWidth(desiredContentWidth) + _documentHorizontalPadding;
    }

    internal static double CalculateAvailableContentWidth(
        double boundsWidth,
        double resizeReservedWidth,
        double documentHorizontalPadding,
        bool useWebRenderer)
    {
        _ = useWebRenderer;
        return SysMath.Max(
            MinManualContentWidth,
            boundsWidth
                - resizeReservedWidth
                - documentHorizontalPadding
                - ViewportHorizontalGutter);
    }

    private void OnHostScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)
    {
        if (_viewModel is not null)
        {
            _lastReadingProgress = e.ProgressPercent;
            _viewModel.ReadingProgress = _lastReadingProgress;
        }
    }

    private void OnHostMinimapStateChanged(object? sender, ApplicateWebMinimapStateEventArgs e)
    {
        var nextReservedWidth = e.Visible ? e.ReservedWidth : 0;
        if (SysMath.Abs(_webMinimapReservedWidth - nextReservedWidth) < 0.5)
        {
            return;
        }

        _webMinimapReservedWidth = nextReservedWidth;
        ApplyColumnWidth();
    }

    private void OnHostWidthDragRequested(object? sender, ApplicateWebWidthDragEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Phase == ApplicateWebWidthDragPhase.Start)
        {
            _isDraggingWidth = true;
            _dragStartWidth = _manualContentWidth ?? _viewModel.ContentWidthSetting;
            return;
        }

        if (!_isDraggingWidth)
        {
            return;
        }

        ApplyWidthDragDelta(e.DeltaX);
        if (e.Phase == ApplicateWebWidthDragPhase.End)
        {
            FinishWidthHandleDrag();
        }
    }

    private void OnHostWheelRequested(object? sender, ApplicateWebWheelEventArgs e)
    {
        var deltaY = NormalizeWebWheelDelta(
            e.DeltaY,
            e.DeltaMode,
            _scroll.SmallChange.Height,
            _scroll.Viewport.Height);
        ScrollByWheelDelta(deltaY);
    }

    private void OnHostViewerInteractionRequested(object? sender, EventArgs e)
    {
        if (_viewModel?.HasOpenOverlay == true)
        {
            _viewModel.CloseOverlayCommand.Execute(null);
        }
    }

    /// <summary>Test seam — exposes the shared-host slot.</summary>
    internal Panel WebSlotForTesting => _webSlot;

    /// <summary>Test seam — exposes the failure overlay visibility.</summary>
    internal bool IsFailureViewVisibleForTesting => _failureView.IsVisible;

    public void Dispose()
    {
        UnwireSharedHostEvents();
        if (_scrollBarOverlay is not null)
        {
            _documentLayer.Children.Remove(_scrollBarOverlay.Control);
            _scrollBarOverlay.Dispose();
            _scrollBarOverlay = null;
        }
        GC.SuppressFinalize(this);
    }
}
