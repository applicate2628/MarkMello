using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using MarkMello.Presentation.ViewModels;
using System.ComponentModel;

namespace MarkMello.Presentation.Views;

public partial class AppUpdatesPanelView : UserControl
{
    private const int StatusContentSettleDelayMs = 70;
    private MainWindowViewModel? _viewModel;
    private int _statusTransitionGeneration;

    public AppUpdatesPanelView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachViewModel(DataContext as MainWindowViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _statusTransitionGeneration++;
        AttachViewModel(null);
        base.OnDetachedFromVisualTree(e);
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
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(MainWindowViewModel.UpdateStatusTitle)
            or nameof(MainWindowViewModel.UpdateStatusMessage)
            or nameof(MainWindowViewModel.UpdateStateBadge)))
        {
            return;
        }

        StartStatusContentTransition();
    }

    private void StartStatusContentTransition()
    {
        var generation = ++_statusTransitionGeneration;
        UpdateStatusContent.Opacity = 0;
        UpdateStatusContent.RenderTransform = TransformOperations.Parse("translate(0px,3px)");
        _ = CompleteStatusContentTransitionAsync(generation);
    }

    private async Task CompleteStatusContentTransitionAsync(int generation)
    {
        await Task.Delay(StatusContentSettleDelayMs).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (generation != _statusTransitionGeneration || _viewModel is null)
                {
                    return;
                }

                UpdateStatusContent.Opacity = 1;
                UpdateStatusContent.RenderTransform = TransformOperations.Identity;
            },
            DispatcherPriority.Background);
    }
}
