using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkMello.Applicate.Desktop.Editing;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Code-only tabs strip rendered above the document area. Subscribes to
/// <see cref="IOpenDocumentsService"/> and reflects the open document
/// list: click a tab to activate, click <c>×</c> to close, click
/// <c>+</c> to open a new file via the OS file picker, and drag a tab
/// horizontally to reorder it within the strip.
/// </summary>
internal sealed class ApplicateTabsView : UserControl
{
    private static readonly FilePickerFileType MarkdownFileType = new("Markdown")
    {
        Patterns = new[] { "*.md", "*.markdown", "*.txt" }
    };

    private static readonly FilePickerFileType[] PickerFilters = new[] { MarkdownFileType };

    // Pointer travel before a press becomes a drag. Below threshold the
    // press is still treated as a click so tab activation keeps working
    // when the user lets go without intentionally dragging.
    private const double DragThresholdPixels = 5.0;

    // Stack panel gap between tab borders (mirrors _tabsPanel.Spacing).
    // When the dragged tab moves N slots over, neighbors translate by
    // (DraggedTabWidth + TabSpacingPixels) to fill the gap.
    private const double TabSpacingPixels = 4.0;

    // Horizontal tab-strip scroll distance per mouse-wheel notch — a little under
    // one tab width, so each notch advances the strip by roughly one tab.
    private const double TabWheelStepPixels = 80.0;

    // Animation timing for non-dragged tabs sliding into a new slot is
    // sourced from the app's motion tokens (Themes/Motion.axaml) so it
    // matches popup fades, hover transitions, and the rest of the UI's
    // pacing in one place.
    private static TimeSpan ReorderAnimationDuration => ApplicateMotion.Standard;

    private readonly IOpenDocumentsService _openDocsService;
    private readonly StackPanel _tabsPanel;
    private readonly ScrollViewer _tabsScroll;
    // Overflow nav: paired edge scroll arrows + a tab-list (⌄) dropdown. All
    // shown only when the tab strip overflows. Visually distinct from the
    // single ‹/› TOC toggle chevron that also lives in this row.
    private readonly Button _scrollLeftButton;
    private readonly Button _scrollRightButton;
    private readonly Avalonia.Controls.Shapes.Path _scrollLeftIcon;
    private readonly Avalonia.Controls.Shapes.Path _scrollRightIcon;
    // Thin separators that fence the overflow scroll arrows off from the tab strip
    // when it overflows, so each arrow reads as a distinct control, not a tab edge.
    private readonly Border _leftScrollSeparator;
    private readonly Border _rightScrollSeparator;
    private readonly Button _tabListButton;
    private readonly Avalonia.Controls.Shapes.Path _tabListIcon;
    private readonly Avalonia.Controls.Primitives.Popup _tabListPopup;
    private readonly StackPanel _tabListItems;
    private readonly Button _addButton;
    private readonly Avalonia.Controls.Shapes.Path _addButtonIcon;
    // v0.3.2 — magnifier toolbar button beside the "+" open-file button.
    // Triggers the renderer's find bar via MainWindowViewModel.OpenFindBarCommand.
    private readonly Button _findButton;
    private readonly Avalonia.Controls.Shapes.Path _findButtonIcon;
    private readonly Dictionary<Control, OpenDocument> _tabToDocument = new();
    private Border? _rootBorder;

    private DragState? _dragState;

    public ApplicateTabsView(IOpenDocumentsService openDocsService)
    {
        _openDocsService = openDocsService ?? throw new ArgumentNullException(nameof(openDocsService));

        MinHeight = 36;
        Background = new SolidColorBrush(Colors.Transparent);

        _tabsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(6, 4, 6, 0)
        };

        _tabsScroll = new ScrollViewer
        {
            // Hidden, not Auto: a visible horizontal scrollbar would sit under
            // the strip in the same row as the tabs and overlap them. The
            // strip scrolls via shift+wheel; the edge arrows + tab-list
            // dropdown below give a discoverable affordance for that scroll.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tabsPanel
        };

        _addButtonIcon = BuildPlusIcon(ResolveBrush("MmTextSoftBrush"));
        _addButton = BuildAddButton(_addButtonIcon);
        ToolTip.SetTip(_addButton, "Open file");
        _addButton.Click += async (_, _) => await OnAddClickAsync().ConfigureAwait(true);

        // v0.3.2 — magnifier button. Placed to the LEFT of the "+" button
        // so the keyboard reading order matches the user's request
        // ("лупу около плюсика, который open file"). Clicking the magnifier
        // sends an open-find-bar IPC to the renderer via the VM-level
        // OpenFindBarCommand (subscribed by ApplicateViewerView). The
        // renderer toggles its existing find bar (commit 4aee666) so a
        // second click closes it — same semantics as Ctrl+F.
        _findButtonIcon = BuildMagnifierIcon(ResolveBrush("MmTextSoftBrush"));
        _findButton = BuildToolbarButton(_findButtonIcon);
        ToolTip.SetTip(_findButton, "Find in document (Ctrl+F)");
        _findButton.Click += OnFindButtonClick;

        // Edge scroll arrows (← / →) — distinct from the TOC ‹/› toggle by
        // glyph (arrows with a shaft), pairing, and edge position. Hidden
        // until the strip overflows; click pages the scroll.
        _scrollLeftIcon = BuildScrollArrowIcon(left: true, ResolveBrush("MmTextSoftBrush"));
        _scrollLeftButton = BuildToolbarButton(_scrollLeftIcon);
        _scrollLeftButton.Width = 16;
        _scrollLeftButton.IsVisible = false;
        ToolTip.SetTip(_scrollLeftButton, "Scroll tabs left");
        _scrollLeftButton.Click += (_, _) => ScrollTabs(-1);

        _scrollRightIcon = BuildScrollArrowIcon(left: false, ResolveBrush("MmTextSoftBrush"));
        _scrollRightButton = BuildToolbarButton(_scrollRightIcon);
        _scrollRightButton.Width = 16;
        _scrollRightButton.IsVisible = false;
        ToolTip.SetTip(_scrollRightButton, "Scroll tabs right");
        _scrollRightButton.Click += (_, _) => ScrollTabs(1);

        _leftScrollSeparator = BuildScrollSeparator();
        _rightScrollSeparator = BuildScrollSeparator();

        // Tab-list dropdown (⌄) — opens a menu of all open tabs to jump to
        // any one directly. Down-glyph + right placement keep it clear of the
        // TOC chevron. Also overflow-only.
        _tabListIcon = BuildTabListIcon(ResolveBrush("MmTextSoftBrush"));
        _tabListButton = BuildToolbarButton(_tabListIcon);
        _tabListButton.IsVisible = false;
        ToolTip.SetTip(_tabListButton, "All open tabs");
        // A plain MenuFlyout renders in the in-window overlay layer, which the
        // WebView2 NativeControlHost HWND occludes (the dropdown was invisible
        // over the document). Use a WINDOWED Popup (ShouldUseOverlayLayer=False)
        // exactly like the app's menu/settings popovers so it floats above the
        // WebView2 HWND as a separate top-level OS window.
        _tabListItems = new StackPanel { Spacing = 1 };
        _tabListPopup = new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = _tabListButton,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true,
            ShouldUseOverlayLayer = false,
            Child = new Border
            {
                Background = ResolveBrush("MmElevatedBackgroundBrush"),
                BorderBrush = ResolveBrush("MmBorderSoftBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = 168,
                Child = _tabListItems
            }
        };
        _tabListButton.Click += (_, _) => OpenTabList();

        var rightCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightCluster.Children.Add(_tabListButton);
        rightCluster.Children.Add(_findButton);
        rightCluster.Children.Add(_addButton);
        rightCluster.Children.Add(_tabListPopup);

        _tabsScroll.ScrollChanged += (_, _) => UpdateTabOverflowChrome();
        // Plain mouse wheel over the tabs area pages the horizontal scroll.
        _tabsScroll.PointerWheelChanged += OnTabsWheel;

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto")
        };
        Grid.SetColumn(_scrollLeftButton, 0);
        Grid.SetColumn(_leftScrollSeparator, 1);
        Grid.SetColumn(_tabsScroll, 2);
        Grid.SetColumn(_rightScrollSeparator, 3);
        Grid.SetColumn(_scrollRightButton, 4);
        Grid.SetColumn(rightCluster, 5);
        root.Children.Add(_scrollLeftButton);
        root.Children.Add(_leftScrollSeparator);
        root.Children.Add(_tabsScroll);
        root.Children.Add(_rightScrollSeparator);
        root.Children.Add(_scrollRightButton);
        root.Children.Add(rightCluster);

        // Bottom border separates the tabs strip from the document body
        // and gives the active tab a clear baseline to "merge" into.
        _rootBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ResolveBrush("MmBorderBrush"),
            Background = ResolveBrush("MmSurfaceBrush"),
            Child = root
        };
        Content = _rootBorder;

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    // Ghost icon "+" button (browser-style new-tab). Transparent bg, no
    // border, vector + icon. Avalonia's default Button styles provide a
    // subtle hover highlight on the transparent surface. Icon stroke uses
    // MmTextSoftBrush — refreshed on theme change via _addButtonIcon.
    private static Button BuildAddButton(Avalonia.Controls.Shapes.Path icon)
    {
        return new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon
        };
    }

    private static Avalonia.Controls.Shapes.Path BuildPlusIcon(IBrush stroke)
    {
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M6,0 L6,12 M0,6 L12,6"),
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    // Magnifier glyph at the same 12x12 footprint as the "+" icon so the
    // two buttons line up visually. The circle sits at top-left, the
    // handle runs to bottom-right. Stroke styling matches the existing
    // tab strip ghost-button language (1.5 px rounded stroke, MmTextSoft
    // brush, re-tinted by ApplyThemeColours below).
    private static Avalonia.Controls.Shapes.Path BuildMagnifierIcon(IBrush stroke)
    {
        return new Avalonia.Controls.Shapes.Path
        {
            // Circle: center (4.5, 4.5), radius 3.5. Handle: from (7.0, 7.0)
            // diagonal to (11.0, 11.0).
            Data = Avalonia.Media.Geometry.Parse(
                "M 8,4.5 A 3.5,3.5 0 1,1 1,4.5 A 3.5,3.5 0 1,1 8,4.5 Z M 7,7 L 11,11"),
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Button BuildToolbarButton(Avalonia.Controls.Shapes.Path icon)
    {
        return new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon
        };
    }

    // Short vertical hairline shown between an overflow scroll arrow and the tab
    // strip. Inset top/bottom so it reads as a deliberate divider, not a full-
    // height border. Hidden until the strip overflows (UpdateTabOverflowChrome).
    private static Border BuildScrollSeparator()
    {
        return new Border
        {
            Width = 1,
            Margin = new Thickness(2, 7, 2, 7),
            Background = ResolveBrush("MmBorderSoftBrush"),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private void OnFindButtonClick(object? sender, RoutedEventArgs e)
    {
        // Route through the VM's command so the find-bar trigger surface
        // is consistent across magnifier click, Ctrl+F keystroke (rendered
        // inside the WebView), and any future menu entry. The viewer view
        // subscribes to OpenFindBarRequested and sends the renderer IPC.
        var window = TopLevel.GetTopLevel(this);
        if (window?.DataContext is MarkMello.Presentation.ViewModels.MainWindowViewModel vm
            && vm.OpenFindBarCommand.CanExecute(null))
        {
            vm.OpenFindBarCommand.Execute(null);
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ((INotifyCollectionChanged)_openDocsService.OpenDocuments).CollectionChanged += OnOpenDocumentsChanged;
        _openDocsService.ActiveDocumentChanged += OnActiveDocumentChanged;
        _openDocsService.DocumentModifiedChanged += OnDocumentModifiedChanged;

        // Tab colours are resolved from the Mm* theme brushes at build time,
        // so a theme switch leaves them frozen at the old palette (light tabs
        // in dark mode). Listen for the application's theme variant change
        // and rebuild the strip with fresh colours each time.
        if (Avalonia.Application.Current is { } app)
        {
            app.ActualThemeVariantChanged += OnThemeVariantChanged;
        }

        Rebuild();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ((INotifyCollectionChanged)_openDocsService.OpenDocuments).CollectionChanged -= OnOpenDocumentsChanged;
        _openDocsService.ActiveDocumentChanged -= OnActiveDocumentChanged;
        _openDocsService.DocumentModifiedChanged -= OnDocumentModifiedChanged;
        if (Avalonia.Application.Current is { } app)
        {
            app.ActualThemeVariantChanged -= OnThemeVariantChanged;
        }
        CancelDrag();
    }

    private static Avalonia.Controls.Shapes.Path BuildScrollArrowIcon(bool left, IBrush stroke)
    {
        // Arrow WITH a shaft (← / →) so it does not read as the TOC ‹/› toggle.
        var data = left
            ? "M 7,3 L 2.5,7.5 L 7,12 M 2.5,7.5 L 12,7.5"
            : "M 6,3 L 10.5,7.5 L 6,12 M 1,7.5 L 10.5,7.5";
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
            Width = 13,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Avalonia.Controls.Shapes.Path BuildTabListIcon(IBrush stroke)
    {
        // Down chevron (⌄) — reads as "open a dropdown", distinct from the
        // left/right TOC toggle chevron.
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M 2,5 L 7,10 L 12,5"),
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
            Width = 14,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void ScrollTabs(int direction)
    {
        if (_tabsScroll.Viewport.Width <= 0)
        {
            return;
        }

        var maxX = System.Math.Max(0, _tabsScroll.Extent.Width - _tabsScroll.Viewport.Width);
        var step = _tabsScroll.Viewport.Width * 0.6;
        var newX = System.Math.Clamp(_tabsScroll.Offset.X + direction * step, 0, maxX);
        _tabsScroll.Offset = new Avalonia.Vector(newX, _tabsScroll.Offset.Y);
    }

    private void OnTabsWheel(object? sender, PointerWheelEventArgs e)
    {
        // Plain mouse wheel over the tabs area pages the strip's horizontal scroll,
        // so an overflowing strip is navigable with the wheel — not only the edge
        // arrows or Shift+wheel. No-op (and left unhandled, so it falls through to
        // default handling) when the strip fits or the wheel is purely horizontal.
        if (e.Delta.Y == 0 || _tabsScroll.Extent.Width <= _tabsScroll.Viewport.Width + 1)
        {
            return;
        }

        var maxX = System.Math.Max(0, _tabsScroll.Extent.Width - _tabsScroll.Viewport.Width);
        // Wheel up (Delta.Y > 0) scrolls toward the start, wheel down toward the end.
        var newX = System.Math.Clamp(
            _tabsScroll.Offset.X - (e.Delta.Y * TabWheelStepPixels), 0, maxX);
        if (System.Math.Abs(newX - _tabsScroll.Offset.X) > 0.01)
        {
            _tabsScroll.Offset = new Avalonia.Vector(newX, _tabsScroll.Offset.Y);
        }

        e.Handled = true;
    }

    private void UpdateTabOverflowChrome()
    {
        var overflow = _tabsScroll.Extent.Width > _tabsScroll.Viewport.Width + 1;
        _scrollLeftButton.IsVisible = overflow;
        _scrollRightButton.IsVisible = overflow;
        _leftScrollSeparator.IsVisible = overflow;
        _rightScrollSeparator.IsVisible = overflow;
        _tabListButton.IsVisible = overflow;
        if (!overflow)
        {
            return;
        }

        var maxX = _tabsScroll.Extent.Width - _tabsScroll.Viewport.Width;
        SetScrollArrowState(_scrollLeftButton, _tabsScroll.Offset.X > 1);
        SetScrollArrowState(_scrollRightButton, _tabsScroll.Offset.X < maxX - 1);
    }

    private static void SetScrollArrowState(Button arrow, bool enabled)
    {
        // At a scroll extreme the arrow goes fully inert: not clickable, not
        // hit-testable (so it can't show a hover highlight), and dimmed — so it
        // reads as "nothing more this way" instead of an active-looking button.
        arrow.IsEnabled = enabled;
        arrow.IsHitTestVisible = enabled;
        arrow.Opacity = enabled ? 1.0 : 0.3;
    }

    private void EnsureActiveTabVisible()
    {
        _tabsScroll.UpdateLayout();
        if (_tabsScroll.Viewport.Width <= 0)
        {
            return;
        }

        Border? activeTab = null;
        foreach (var tab in _tabsPanel.Children.OfType<Border>())
        {
            if (_tabToDocument.TryGetValue(tab, out var doc)
                && ReferenceEquals(doc, _openDocsService.ActiveDocument))
            {
                activeTab = tab;
                break;
            }
        }

        if (activeTab is null || activeTab.Bounds.Width <= 0)
        {
            return;
        }

        var tabLeft = activeTab.Bounds.X;
        var tabRight = tabLeft + activeTab.Bounds.Width;
        var viewLeft = _tabsScroll.Offset.X;
        var viewRight = viewLeft + _tabsScroll.Viewport.Width;

        double newX;
        if (tabLeft < viewLeft)
        {
            newX = tabLeft;
        }
        else if (tabRight > viewRight)
        {
            newX = tabRight - _tabsScroll.Viewport.Width;
        }
        else
        {
            return;
        }

        var maxX = System.Math.Max(0, _tabsScroll.Extent.Width - _tabsScroll.Viewport.Width);
        _tabsScroll.Offset = new Avalonia.Vector(System.Math.Clamp(newX, 0, maxX), _tabsScroll.Offset.Y);
    }

    private void OpenTabList()
    {
        if (_tabListPopup.Child is Border border)
        {
            border.Background = ResolveBrush("MmElevatedBackgroundBrush");
            border.BorderBrush = ResolveBrush("MmBorderSoftBrush");
        }

        _tabListItems.Children.Clear();
        foreach (var doc in _openDocsService.OpenDocuments)
        {
            var target = doc;
            var label = new TextBlock
            {
                Text = target.DisplayName,
                Foreground = ResolveBrush("MmTextBrush"),
                FontWeight = ReferenceEquals(target, _openDocsService.ActiveDocument)
                    ? FontWeight.SemiBold
                    : FontWeight.Normal,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };
            var item = new Button
            {
                Classes = { "mm-menu-item" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = label
            };
            item.Click += (_, _) =>
            {
                _tabListPopup.IsOpen = false;
                _openDocsService.Activate(target);
            };
            _tabListItems.Children.Add(item);
        }

        _tabListPopup.IsOpen = true;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(ApplyThemeColours);

    private void ApplyThemeColours()
    {
        // Refresh ALL theme-bound brushes in place. Rebuilding the tabs
        // strip on theme change causes a visible chunky transition because
        // children are recreated sequentially; updating in place re-tints
        // every tab atomically.
        if (_rootBorder is not null)
        {
            _rootBorder.BorderBrush = ResolveBrush("MmBorderBrush");
            _rootBorder.Background = ResolveBrush("MmSurfaceBrush");
        }
        // Ghost "+" button has transparent bg + no border, only the icon
        // stroke is theme-dependent. Refresh in place so theme switch
        // re-tints the + without recreating the button. Magnifier icon
        // follows the same pattern (sibling to "+", same stroke brush).
        _addButtonIcon.Stroke = ResolveBrush("MmTextSoftBrush");
        _findButtonIcon.Stroke = ResolveBrush("MmTextSoftBrush");
        _scrollLeftIcon.Stroke = ResolveBrush("MmTextSoftBrush");
        _scrollRightIcon.Stroke = ResolveBrush("MmTextSoftBrush");
        _tabListIcon.Stroke = ResolveBrush("MmTextSoftBrush");
        _leftScrollSeparator.Background = ResolveBrush("MmBorderSoftBrush");
        _rightScrollSeparator.Background = ResolveBrush("MmBorderSoftBrush");

        var borderBrush = ResolveBrush("MmBorderBrush");
        var activeBg = ResolveBrush("MmBackgroundBrush");
        var inactiveBg = ResolveBrush("MmSurfaceBrush");
        var textBrush = ResolveBrush("MmTextBrush");
        foreach (var tab in _tabsPanel.Children.OfType<Border>())
        {
            var doc = _tabToDocument.TryGetValue(tab, out var d) ? d : null;
            var isActive = doc is not null
                && ReferenceEquals(doc, _openDocsService.ActiveDocument);
            tab.BorderBrush = borderBrush;
            tab.Background = isActive ? activeBg : inactiveBg;
            // Update the tab label's Foreground too. Without this, the
            // captured brush from BuildTab stays at the previous theme
            // palette (e.g. dark text on dark surface after a Light->Dark
            // switch) and the tab title becomes near-invisible.
            if (tab.Child is Grid grid)
            {
                foreach (var child in grid.Children)
                {
                    if (child is TextBlock label)
                    {
                        label.Foreground = textBrush;
                    }
                }
            }
        }
    }

    private void OnOpenDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(Rebuild);

    private void OnActiveDocumentChanged(object? sender, ActiveDocumentChangedEventArgs e)
        => Dispatcher.UIThread.Post(Rebuild);

    private void OnDocumentModifiedChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        CancelDrag();
        _tabsPanel.Children.Clear();
        _tabToDocument.Clear();

        foreach (var doc in _openDocsService.OpenDocuments)
        {
            var tab = BuildTab(doc);
            _tabToDocument[tab] = doc;
            _tabsPanel.Children.Add(tab);
        }

        // After the strip relayouts with the new tab set: scroll the (possibly
        // off-screen) active tab into view, then refresh the overflow chrome. A
        // Rebuild resets the scroll offset, so activating a hidden tab (e.g.
        // from the tab-list dropdown) must re-reveal its tab button.
        Dispatcher.UIThread.Post(
            () =>
            {
                EnsureActiveTabVisible();
                UpdateTabOverflowChrome();
            },
            DispatcherPriority.Background);
    }

    private Control BuildTab(OpenDocument doc)
    {
        var isActive = ReferenceEquals(doc, _openDocsService.ActiveDocument);

        // Dirty marker: a leading "●" when the document has unsaved edits
        // (OpenDocument.IsModified, mirrored from the active session's dirty
        // state by the active-document bridge).
        var label = new TextBlock
        {
            Text = (doc.IsModified ? "●  " : string.Empty) + doc.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = isActive
                ? ResolveBrush("MmTextBrush")
                : ResolveBrush("MmTextBrush")
        };

        var closeButton = new Button
        {
            Content = "×",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            FontSize = 14,
            CornerRadius = new CornerRadius(3)
        };
        ToolTip.SetTip(closeButton, "Close");
        closeButton.Click += (_, e) => OnCloseClicked(doc, e);

        var tabContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // Reserve the active (SemiBold) label width on every tab via an
        // invisible always-SemiBold sizer behind the live label, so selecting a
        // tab no longer widens it and shifts the whole strip. The live label
        // keeps its Normal/SemiBold weight for the visual accent.
        var labelSizer = new TextBlock
        {
            Text = doc.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            FontWeight = FontWeight.SemiBold,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Grid.SetColumn(labelSizer, 0);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(closeButton, 1);
        tabContent.Children.Add(labelSizer);
        tabContent.Children.Add(label);
        tabContent.Children.Add(closeButton);

        // Active tab pops out: brighter background (MmBackgroundBrush) than
        // the strip surface, and merges into the body below by having no
        // bottom border. Inactive tabs use the muted surface tone and a
        // full border so they read as "behind" the active one.
        var tab = new Border
        {
            Padding = new Thickness(0),
            MinWidth = 100,
            MinHeight = 28,
            Child = tabContent,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Background = isActive
                ? ResolveBrush("MmBackgroundBrush")
                : ResolveBrush("MmSurfaceBrush"),
            BorderBrush = ResolveBrush("MmBorderBrush"),
            BorderThickness = isActive
                ? new Thickness(1, 1, 1, 0)
                : new Thickness(1, 1, 1, 1)
        };
        ToolTip.SetTip(tab, doc.FilePath);

        tab.PointerPressed += (s, e) => OnTabPointerPressed(tab, doc, e);
        tab.PointerMoved += (s, e) => OnTabPointerMoved(tab, e);
        tab.PointerReleased += (s, e) => OnTabPointerReleased(tab, doc, e);
        tab.PointerCaptureLost += (s, e) => CancelDrag();
        tab.ContextMenu = BuildTabContextMenu(doc);

        return tab;
    }

    private ContextMenu BuildTabContextMenu(OpenDocument doc)
    {
        // Standard tab context menu: close current/others/right/all, then
        // path utilities. Items rebuild per tab because IsEnabled depends
        // on the document's position in the strip at the moment the menu
        // opens.
        var menu = new ContextMenu();

        var closeItem = new MenuItem { Header = "Close" };
        closeItem.Click += (_, _) => CloseDocument(doc);

        var closeOthersItem = new MenuItem
        {
            Header = "Close Others",
            IsEnabled = _openDocsService.OpenDocuments.Count > 1
        };
        closeOthersItem.Click += (_, _) => CloseOthers(doc);

        var closeToRightItem = new MenuItem
        {
            Header = "Close to the Right",
            IsEnabled = _openDocsService.OpenDocuments.IndexOf(doc) <
                        _openDocsService.OpenDocuments.Count - 1
        };
        closeToRightItem.Click += (_, _) => CloseToRight(doc);

        var closeAllItem = new MenuItem { Header = "Close All" };
        closeAllItem.Click += (_, _) => CloseAll();

        var copyPathItem = new MenuItem { Header = "Copy Path" };
        copyPathItem.Click += async (_, _) => await CopyPathAsync(doc).ConfigureAwait(true);

        var revealItem = new MenuItem { Header = "Reveal in File Explorer" };
        revealItem.Click += (_, _) => RevealInExplorer(doc);

        menu.Items.Add(closeItem);
        menu.Items.Add(closeOthersItem);
        menu.Items.Add(closeToRightItem);
        menu.Items.Add(closeAllItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyPathItem);
        menu.Items.Add(revealItem);

        return menu;
    }

    private void CloseDocument(OpenDocument doc)
    {
        // Same logic as the × button: route active-tab close through the
        // VM so the dirty-prompt can prevent it; non-active close goes
        // through the service directly.
        if (ReferenceEquals(doc, _openDocsService.ActiveDocument)
            && TopLevel.GetTopLevel(this)?.DataContext
                is MarkMello.Presentation.ViewModels.MainWindowViewModel vm
            && vm.CloseFileCommand.CanExecute(null))
        {
            vm.CloseFileCommand.Execute(null);
            return;
        }
        _openDocsService.Close(doc);
    }

    private void CloseOthers(OpenDocument keep)
    {
        // Snapshot before mutating; the collection changes underneath as
        // each Close fires CollectionChanged → Rebuild → menu rebuild.
        var toClose = _openDocsService.OpenDocuments
            .Where(d => !ReferenceEquals(d, keep))
            .ToList();
        foreach (var doc in toClose)
        {
            _openDocsService.Close(doc);
        }
    }

    private void CloseToRight(OpenDocument anchor)
    {
        var anchorIndex = _openDocsService.OpenDocuments.IndexOf(anchor);
        if (anchorIndex < 0)
        {
            return;
        }
        var toClose = _openDocsService.OpenDocuments
            .Skip(anchorIndex + 1)
            .ToList();
        foreach (var doc in toClose)
        {
            _openDocsService.Close(doc);
        }
    }

    private void CloseAll()
    {
        var toClose = _openDocsService.OpenDocuments.ToList();
        foreach (var doc in toClose)
        {
            _openDocsService.Close(doc);
        }
    }

    private async System.Threading.Tasks.Task CopyPathAsync(OpenDocument doc)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }
        await clipboard.SetTextAsync(doc.FilePath).ConfigureAwait(true);
    }

    private static void RevealInExplorer(OpenDocument doc)
    {
        if (string.IsNullOrEmpty(doc.FilePath))
        {
            return;
        }

        try
        {
            if (System.OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{doc.FilePath}\"");
            }
            else if (System.OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", new[] { "-R", doc.FilePath });
            }
            else if (System.OperatingSystem.IsLinux())
            {
                var dir = System.IO.Path.GetDirectoryName(doc.FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Diagnostics.Process.Start("xdg-open", dir);
                }
            }
        }
        catch (System.Exception)
        {
            // No reliable fallback if the file manager cannot be launched;
            // swallow so the menu does not crash the host.
        }
    }

    private void OnCloseClicked(OpenDocument doc, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        CloseDocument(doc);
    }

    private void OnTabPointerPressed(Border tab, OpenDocument doc, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var initialIndex = _openDocsService.OpenDocuments.IndexOf(doc);
        if (initialIndex < 0)
        {
            return;
        }

        _dragState = new DragState(
            Document: doc,
            Tab: tab,
            PressedInStrip: e.GetPosition(_tabsPanel),
            IsActiveDrag: false,
            InitialIndex: initialIndex,
            DraggedTabWidth: tab.Bounds.Width,
            CurrentVisualIndex: initialIndex);
    }

    private void OnTabPointerMoved(Border tab, PointerEventArgs e)
    {
        if (_dragState is null || !ReferenceEquals(_dragState.Tab, tab))
        {
            return;
        }

        var current = e.GetPosition(_tabsPanel);
        if (!_dragState.IsActiveDrag)
        {
            var dx0 = current.X - _dragState.PressedInStrip.X;
            var dy0 = current.Y - _dragState.PressedInStrip.Y;
            if (System.Math.Sqrt((dx0 * dx0) + (dy0 * dy0)) < DragThresholdPixels)
            {
                return;
            }

            _dragState = _dragState with
            {
                IsActiveDrag = true,
                DraggedTabWidth = tab.Bounds.Width
            };
            e.Pointer.Capture(tab);

            // Lift the dragged tab above neighbors so its body slides over
            // them instead of being clipped, and hint at "picked up" state
            // with a slight opacity change. The dragged tab itself has no
            // RenderTransform Transitions because the cursor must track it
            // instantly; only the OTHER tabs animate during reorder.
            tab.ZIndex = 100;
            tab.Opacity = 0.92;
            InstallReorderTransitionsOnNeighbors();
        }

        // Translate the dragged tab to follow the cursor.
        var dx = current.X - _dragState.PressedInStrip.X;
        tab.RenderTransform = new TranslateTransform(dx, 0);

        // Recompute target index from the dragged tab's CURRENT visual
        // center, not from the cursor X. Otherwise the press-anchor
        // offset inside the tab biases the drop trigger: a user who
        // grabs the left edge and drags right would see the tab body
        // overlap the neighbor BEFORE the cursor crosses that
        // neighbor's center, so displacement would not fire until too
        // late. Using the dragged tab's center makes the trigger
        // boundary match the visual edge of the dragged tab.
        var draggedVisualCenterX = tab.Bounds.X + (tab.Bounds.Width / 2.0) + dx;
        var newVisualIndex = ComputeTargetIndexFromVisualCenter(draggedVisualCenterX);
        if (newVisualIndex != _dragState.CurrentVisualIndex)
        {
            _dragState = _dragState with { CurrentVisualIndex = newVisualIndex };
            ApplyNeighborDisplacement(newVisualIndex);
        }
    }

    private void OnTabPointerReleased(Border tab, OpenDocument doc, PointerReleasedEventArgs e)
    {
        if (_dragState is null || !ReferenceEquals(_dragState.Tab, tab))
        {
            // Released without an active drag context (or on a different
            // tab — Avalonia routed it here because we captured pointer).
            ActivateIfClickOnly(doc);
            CancelDrag();
            return;
        }

        if (!_dragState.IsActiveDrag)
        {
            // Below drag threshold → treat as a plain click and activate.
            ActivateIfClickOnly(doc);
            CancelDrag();
            return;
        }

        // Snapshot ALL needed dragState fields BEFORE releasing pointer
        // capture. Capture(null) raises PointerCaptureLost on the tab,
        // which calls CancelDrag and nulls _dragState — any subsequent
        // _dragState.* dereference throws NullReferenceException.
        var draggedDocument = _dragState.Document;
        var targetIndex = _dragState.CurrentVisualIndex;
        var initialIndex = _dragState.InitialIndex;
        e.Pointer.Capture(null);

        if (targetIndex >= 0 && targetIndex != initialIndex)
        {
            _openDocsService.Move(draggedDocument, targetIndex);
            // Rebuild fires from CollectionChanged → fresh borders without
            // transforms or transitions. CancelDrag below is then a no-op
            // because Rebuild already cleared _dragState.
        }

        CancelDrag();
    }

    private void ActivateIfClickOnly(OpenDocument doc)
    {
        _openDocsService.Activate(doc);
    }

    private int ComputeTargetIndexFromVisualCenter(double draggedCenterX)
    {
        if (_dragState is null)
        {
            return -1;
        }

        // For each non-dragged tab, take its ORIGINAL (pre-drag) center X
        // (i.e. its layout position with the dragged tab still in place).
        // The dragged tab's final index is the count of non-dragged tabs
        // whose original centers are to the left of the dragged tab's
        // visual center.
        var targetIndex = 0;
        for (var i = 0; i < _tabsPanel.Children.Count; i++)
        {
            var child = _tabsPanel.Children[i] as Control;
            if (child is null || ReferenceEquals(child, _dragState.Tab))
            {
                continue;
            }

            // child.Bounds.X is the StackPanel-layout X (transforms do not
            // affect Bounds). That is exactly what we want — pre-drag X.
            var centerX = child.Bounds.X + (child.Bounds.Width / 2.0);
            if (draggedCenterX > centerX)
            {
                targetIndex++;
            }
        }

        return targetIndex;
    }

    private void InstallReorderTransitionsOnNeighbors()
    {
        if (_dragState?.Tab is not { } draggedTab)
        {
            return;
        }

        foreach (var child in _tabsPanel.Children.OfType<Border>())
        {
            if (ReferenceEquals(child, draggedTab))
            {
                continue;
            }

            child.Transitions ??= new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = ReorderAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            };
        }
    }

    private void ApplyNeighborDisplacement(int visualIndex)
    {
        if (_dragState is null)
        {
            return;
        }

        var displacement = _dragState.DraggedTabWidth + TabSpacingPixels;
        var initial = _dragState.InitialIndex;

        for (var i = 0; i < _tabsPanel.Children.Count; i++)
        {
            var child = _tabsPanel.Children[i] as Border;
            if (child is null || ReferenceEquals(child, _dragState.Tab))
            {
                continue;
            }

            // Logic:
            //  - If dragged moves LEFT (visualIndex < initial), tabs with
            //    original index in [visualIndex, initial-1] shift RIGHT
            //    by `displacement` to vacate space at visualIndex.
            //  - If dragged moves RIGHT (visualIndex > initial), tabs in
            //    [initial+1, visualIndex] shift LEFT by `displacement`.
            //  - All other tabs stay put (RenderTransform = identity).
            double delta = 0;
            if (visualIndex < initial && i >= visualIndex && i < initial)
            {
                delta = displacement;
            }
            else if (visualIndex > initial && i > initial && i <= visualIndex)
            {
                delta = -displacement;
            }

            // TransformOperations is required for the transition to lerp
            // smoothly. TranslateTransform on its own does not animate via
            // TransformOperationsTransition.
            child.RenderTransform = TransformOperations.Parse(
                $"translate({delta.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
        }
    }

    private void ClearNeighborDisplacement()
    {
        if (_dragState?.Tab is not { } draggedTab)
        {
            return;
        }

        foreach (var child in _tabsPanel.Children.OfType<Border>())
        {
            if (ReferenceEquals(child, draggedTab))
            {
                continue;
            }

            child.RenderTransform = TransformOperations.Identity;
        }
    }

    private void CancelDrag()
    {
        // Restore the dragged tab's visual to default. Rebuild() recreates
        // the Border on a successful Move, but if the drag is cancelled
        // (pointer capture lost, no Move, or sub-threshold release) the
        // same Border instance keeps its transform, so we must reset.
        if (_dragState?.Tab is { } draggedTab)
        {
            draggedTab.RenderTransform = null;
            draggedTab.ZIndex = 0;
            draggedTab.Opacity = 1.0;
            ClearNeighborDisplacement();
        }
        _dragState = null;
    }

    private async System.Threading.Tasks.Task OnAddClickAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown file",
            AllowMultiple = false,
            FileTypeFilter = PickerFilters
        }).ConfigureAwait(true);

        if (files is null || files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await _openDocsService.OpenAsync(path).ConfigureAwait(true);
    }

    private sealed record DragState(
        OpenDocument Document,
        Border Tab,
        Point PressedInStrip,
        bool IsActiveDrag,
        int InitialIndex,
        double DraggedTabWidth,
        int CurrentVisualIndex);

    private static IBrush ResolveBrush(string resourceKey)
    {
        // Resolve the named SolidColorBrush from the active Avalonia theme
        // resources at build time. The brush picks up the current theme
        // variant (Light/Dark) when the tab is built. Theme changes during
        // a session rebuild the tab strip via Activate/Rebuild paths, so a
        // re-resolve happens on each Rebuild. Fallback color (#888) keeps
        // tabs readable if the resource is missing (e.g. fork-only build
        // running before upstream theme is loaded).
        var app = Avalonia.Application.Current;
        if (app is not null
            && app.TryGetResource(resourceKey, app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }
}
