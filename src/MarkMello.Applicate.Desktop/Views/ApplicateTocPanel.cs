using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Avalonia-side Table of Contents panel for the Applicate fork. Lives at
/// the shell level (installed by <see cref="ApplicateMainWindow"/> as a
/// column to the left of the document body), spans the full content-area
/// height, has its own ScrollViewer so mouse-wheel input scrolls the TOC
/// independently of the document, and is resizable via the adjacent
/// GridSplitter.
///
/// <para>The panel subscribes to <see cref="MainWindowViewModel.DocumentHeadings"/>
/// and <see cref="MainWindowViewModel.ActiveHeadingId"/>. A row click raises
/// <see cref="MainWindowViewModel.ScrollToHeadingCommand"/> which the
/// Applicate-side viewer subscriber forwards as a renderer IPC.</para>
///
/// <para>Visual design: indent by heading level (12 px per level), 12-13 px
/// font, hover background = MmSurfaceHoverBrush, active row background =
/// MmAccentSoftBrush with MmAccentBrush text. Brushes are resolved at
/// theme-change time via <c>TryFindResource</c> so the panel matches the
/// rest of the shell chrome and re-tints atomically on theme switch.</para>
/// </summary>
public sealed class ApplicateTocPanel : UserControl
{
    private readonly ScrollViewer _scroll;
    private readonly ItemsControl _itemsControl;
    private readonly TextBlock _emptyState;
    private readonly Border _rootBorder;
    private readonly Border _separator;
    private MainWindowViewModel? _viewModel;
    private readonly Dictionary<string, Border> _rowsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rowIndexById = new(StringComparer.Ordinal);
    private string? _pendingActiveHeadingScrollReplayId;
    private bool _activeHeadingScrollReplayArmed;

    // Cached theme brushes — refreshed in ApplyThemeBrushes() on attach and
    // on every theme-variant change. A single resolve cycle covers every
    // visual element so theme switches re-tint atomically rather than
    // racing per-element brush lookups.
    private IBrush _backgroundBrush = Brushes.Transparent;
    private IBrush _borderSoftBrush = Brushes.Gray;
    private IBrush _textBrush = Brushes.Black;
    private IBrush _textFaintBrush = Brushes.Gray;
    private IBrush _textSoftBrush = Brushes.DarkGray;
    private IBrush _accentBrush = Brushes.OrangeRed;
    private IBrush _accentSoftBrush = Brushes.LightYellow;
    private IBrush _hoverBrush = Brushes.LightGray;

    public ApplicateTocPanel()
    {
        UseLayoutRounding = true;

        _emptyState = new TextBlock
        {
            Margin = new Thickness(16, 16, 16, 16),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            IsVisible = false,
        };

        _itemsControl = new ItemsControl
        {
            // Top padding = 8 keeps first heading row visually breathing
            // against the panel top edge without re-introducing the header
            // bar. The close button overlay (top-right) sits in the same
            // visual band but is hit-test isolated.
            Margin = new Thickness(4, 8, 4, 16),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
            {
                Orientation = Orientation.Vertical,
            }),
            ItemTemplate = new FuncDataTemplate<DocumentHeading>(
                (heading, _) => BuildHeadingRow(heading),
                false),
        };

        var scrollContent = new Grid();
        scrollContent.Children.Add(_itemsControl);
        scrollContent.Children.Add(_emptyState);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // Avalonia's ScrollViewer consumes wheel input over its area
            // automatically — independent scroll vs. the document body
            // is the default behavior, no extra plumbing needed.
            Content = scrollContent,
        };

        // No collapse affordance inside the panel — the persistent chevron
        // lives in the tabs row of ApplicateMainWindow and flips its glyph
        // («‹» when open, «›» when closed) so a single control toggles
        // visibility in both directions. Keeps the panel content area free
        // of competing visuals over heading rows.

        _separator = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 1,
            IsHitTestVisible = false,
        };

        var rootGrid = new Grid { UseLayoutRounding = true };
        // Scroll fills entire panel; separator is the rightmost 1 px column.
        rootGrid.Children.Add(_scroll);
        rootGrid.Children.Add(_separator);

        _rootBorder = new Border
        {
            UseLayoutRounding = true,
            Child = rootGrid,
        };

        Content = _rootBorder;

        DataContextChanged += OnDataContextChangedHandler;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (global::Avalonia.Application.Current is { } app)
        {
            app.ActualThemeVariantChanged += OnThemeVariantChanged;
        }
        ApplyThemeBrushes();
        AttachViewModel(DataContext as MainWindowViewModel);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ClearActiveHeadingScrollReplay();
        if (global::Avalonia.Application.Current is { } app)
        {
            app.ActualThemeVariantChanged -= OnThemeVariantChanged;
        }
        AttachViewModel(null);
    }

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        AttachViewModel(DataContext as MainWindowViewModel);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(ApplyThemeBrushes, DispatcherPriority.Background);

    private void ApplyThemeBrushes()
    {
        _backgroundBrush = ResolveBrush("MmBackgroundBrush", _backgroundBrush);
        _borderSoftBrush = ResolveBrush("MmBorderSoftBrush", _borderSoftBrush);
        _textBrush = ResolveBrush("MmTextBrush", _textBrush);
        _textFaintBrush = ResolveBrush("MmTextFaintBrush", _textFaintBrush);
        _textSoftBrush = ResolveBrush("MmTextSoftBrush", _textSoftBrush);
        _accentBrush = ResolveBrush("MmAccentBrush", _accentBrush);
        _accentSoftBrush = ResolveBrush("MmAccentSoftBrush", _accentSoftBrush);
        _hoverBrush = ResolveBrush("MmSurfaceHoverBrush", _hoverBrush);

        _rootBorder.Background = _backgroundBrush;
        _separator.Background = _borderSoftBrush;
        _emptyState.Foreground = _textSoftBrush;

        // Re-tint rows in place — collection-changed handler rebuilds rows
        // when headings change, so an in-place re-tint here only fires on
        // theme switches.
        foreach (var row in _rowsById.Values)
        {
            RefreshRowVisuals(row);
        }
    }

    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush)
        {
            return brush;
        }
        if (global::Avalonia.Application.Current?.TryGetResource(
                key,
                global::Avalonia.Application.Current.ActualThemeVariant,
                out var appResource) == true && appResource is IBrush appBrush)
        {
            return appBrush;
        }
        return fallback;
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        ClearActiveHeadingScrollReplay();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.DocumentHeadings.CollectionChanged -= OnHeadingsCollectionChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.DocumentHeadings.CollectionChanged += OnHeadingsCollectionChanged;
            _emptyState.Text = _viewModel.TocPanelEmpty;
            RebuildRows(_viewModel.DocumentHeadings);
            UpdateEmptyState();
            HighlightActiveHeading(_viewModel.ActiveHeadingId);
        }
        else
        {
            _itemsControl.ItemsSource = null;
            _rowsById.Clear();
            _rowIndexById.Clear();
            _emptyState.IsVisible = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActiveHeadingId))
        {
            if (_viewModel is not null)
            {
                HighlightActiveHeading(_viewModel.ActiveHeadingId);
            }
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsTocVisible))
        {
            if (_viewModel?.IsTocVisible == true)
            {
                RequestActiveHeadingScroll(_viewModel.ActiveHeadingId, allowVirtualizedScroll: true);
            }
            else
            {
                ClearActiveHeadingScrollReplay();
            }
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.TocPanelEmpty) && _viewModel is not null)
        {
            _emptyState.Text = _viewModel.TocPanelEmpty;
            return;
        }

        // The bound headings collection raises CollectionChanged for live
        // updates; PropertyChanged on DocumentHeadings only fires if the
        // entire collection reference is replaced. Handle that path too so
        // a future "swap collection wholesale" code path remains supported.
        if (e.PropertyName == nameof(MainWindowViewModel.DocumentHeadings))
        {
            if (_viewModel is not null)
            {
                _viewModel.DocumentHeadings.CollectionChanged -= OnHeadingsCollectionChanged;
                _viewModel.DocumentHeadings.CollectionChanged += OnHeadingsCollectionChanged;
                RebuildRows(_viewModel.DocumentHeadings);
                UpdateEmptyState();
                HighlightActiveHeading(_viewModel.ActiveHeadingId);
            }
        }
    }

    private void OnHeadingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        RebuildRows(_viewModel.DocumentHeadings);
        UpdateEmptyState();
        HighlightActiveHeading(_viewModel.ActiveHeadingId);
    }

    private void RebuildRows(IEnumerable<DocumentHeading> headings)
    {
        _rowsById.Clear();
        _rowIndexById.Clear();
        var index = 0;
        foreach (var heading in headings)
        {
            _rowIndexById[heading.Id] = index;
            index++;
        }
        _itemsControl.ItemsSource = headings;
    }

    private Border BuildHeadingRow(DocumentHeading? heading)
    {
        if (heading is null)
        {
            // Avalonia can invoke the item template with null while recycling
            // virtualized containers during ItemsSource replacement.
            return new Border
            {
                IsVisible = false,
                IsHitTestVisible = false,
            };
        }

        var levelDot = new Ellipse
        {
            Width = 4,
            Height = 4,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = _textFaintBrush,
        };

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(heading.Text) ? heading.Id : heading.Text,
            FontSize = heading.Level <= 1 ? 13 : 12,
            FontWeight = heading.Level <= 1 ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = _textBrush,
        };

        var contentGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(8 + heading.Indent, 0, 8, 0),
        };
        Grid.SetColumn(levelDot, 0);
        Grid.SetColumn(text, 1);
        contentGrid.Children.Add(levelDot);
        contentGrid.Children.Add(text);

        var row = new Border
        {
            Padding = new Thickness(4, 6, 4, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Margin = new Thickness(4, 1, 4, 1),
            Tag = heading,
            Child = contentGrid,
        };

        _rowsById[heading.Id] = row;
        row.PointerEntered += OnRowPointerEntered;
        row.PointerExited += OnRowPointerExited;
        row.PointerPressed += OnRowPointerPressed;
        row.DetachedFromVisualTree += OnRowDetached;
        RefreshRowVisuals(row);

        return row;
    }

    private void OnRowDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Border row || row.Tag is not DocumentHeading heading)
        {
            return;
        }

        if (_rowsById.TryGetValue(heading.Id, out var current) && ReferenceEquals(current, row))
        {
            _rowsById.Remove(heading.Id);
        }
    }

    private void OnRowPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border row)
        {
            return;
        }
        if (IsActiveRow(row))
        {
            return;
        }
        row.Background = _hoverBrush;
    }

    private void OnRowPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border row)
        {
            return;
        }
        if (IsActiveRow(row))
        {
            return;
        }
        row.Background = Brushes.Transparent;
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not DocumentHeading heading || _viewModel is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        if (_viewModel.ScrollToHeadingCommand.CanExecute(heading.Id))
        {
            _viewModel.ScrollToHeadingCommand.Execute(heading.Id);
        }
    }

    private void UpdateEmptyState()
    {
        var count = _viewModel?.DocumentHeadings.Count ?? 0;
        _emptyState.IsVisible = count == 0;
    }

    private bool IsActiveRow(Border row)
    {
        if (_viewModel is null)
        {
            return false;
        }
        return row.Tag is DocumentHeading heading
               && !string.IsNullOrEmpty(_viewModel.ActiveHeadingId)
               && string.Equals(heading.Id, _viewModel.ActiveHeadingId, StringComparison.Ordinal);
    }

    private void HighlightActiveHeading(string? activeId)
        => HighlightActiveHeading(activeId, allowVirtualizedScroll: true);

    private void HighlightActiveHeading(string? activeId, bool allowVirtualizedScroll)
    {
        foreach (var pair in _rowsById)
        {
            var row = pair.Value;
            var isActive = !string.IsNullOrEmpty(activeId)
                           && string.Equals(pair.Key, activeId, StringComparison.Ordinal);
            ApplyRowVisuals(row, isActive);
        }

        RequestActiveHeadingScroll(activeId, allowVirtualizedScroll);
    }

    private void RequestActiveHeadingScroll(string? activeId, bool allowVirtualizedScroll)
    {
        if (string.IsNullOrEmpty(activeId) || _viewModel?.IsTocVisible != true)
        {
            ClearActiveHeadingScrollReplay();
            return;
        }

        if (!_rowIndexById.TryGetValue(activeId, out var index))
        {
            ClearActiveHeadingScrollReplay();
            return;
        }

        if (!IsVisible || !_scroll.IsAttachedToVisualTree() || _scroll.Bounds.Height <= 0)
        {
            ArmActiveHeadingScrollReplay(activeId);
            return;
        }

        ClearActiveHeadingScrollReplay();
        if (_rowsById.TryGetValue(activeId, out var activeRow))
        {
            var rowToScroll = activeRow;
            Dispatcher.UIThread.Post(() => ScrollRowIntoView(rowToScroll), DispatcherPriority.Background);
        }
        else if (allowVirtualizedScroll)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    _itemsControl.ScrollIntoView(index);
                    Dispatcher.UIThread.Post(() => HighlightActiveHeading(activeId, allowVirtualizedScroll: false), DispatcherPriority.Background);
                },
                DispatcherPriority.Background);
        }
    }

    private void ArmActiveHeadingScrollReplay(string activeId)
    {
        _pendingActiveHeadingScrollReplayId = activeId;
        if (_activeHeadingScrollReplayArmed)
        {
            return;
        }

        _scroll.LayoutUpdated += OnScrollLayoutUpdatedForActiveHeadingReplay;
        _activeHeadingScrollReplayArmed = true;
    }

    private void ClearActiveHeadingScrollReplay()
    {
        if (_activeHeadingScrollReplayArmed)
        {
            _scroll.LayoutUpdated -= OnScrollLayoutUpdatedForActiveHeadingReplay;
            _activeHeadingScrollReplayArmed = false;
        }

        _pendingActiveHeadingScrollReplayId = null;
    }

    private void OnScrollLayoutUpdatedForActiveHeadingReplay(object? sender, EventArgs e)
    {
        var activeId = _pendingActiveHeadingScrollReplayId;
        ClearActiveHeadingScrollReplay();
        RequestActiveHeadingScroll(activeId, allowVirtualizedScroll: true);
    }

    private void RefreshRowVisuals(Border row)
    {
        ApplyRowVisuals(row, IsActiveRow(row));
    }

    private void ApplyRowVisuals(Border row, bool isActive)
    {
        if (row.Tag is not DocumentHeading heading || row.Child is not Grid grid)
        {
            return;
        }

        if (isActive)
        {
            row.Background = _accentSoftBrush;
        }
        else
        {
            row.Background = Brushes.Transparent;
        }

        foreach (var child in grid.Children)
        {
            switch (child)
            {
                case TextBlock textBlock:
                    textBlock.Foreground = isActive ? _accentBrush : _textBrush;
                    textBlock.FontWeight = isActive || heading.Level <= 1
                        ? FontWeight.SemiBold
                        : FontWeight.Normal;
                    break;
                case Ellipse ellipse:
                    ellipse.Fill = isActive ? _accentBrush : _textFaintBrush;
                    break;
            }
        }
    }

    private void ScrollRowIntoView(Border row)
    {
        if (!row.IsVisible || !row.IsAttachedToVisualTree())
        {
            return;
        }
        // Avalonia's BringIntoView walks ancestors and asks each scroll
        // viewer to expose the target rectangle. Use that rather than
        // computing offsets manually so the behaviour matches the rest of
        // the shell's scroll affordances.
        row.BringIntoView();
    }
}
