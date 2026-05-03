using Avalonia.Controls;
using MarkMello.Presentation.Views;

namespace MarkMello.Presentation.Tests;

public sealed class MainWindowOverlayTests
{
    [Fact]
    public void OverlayPopupInteractionSourceIncludesComboBoxItem()
    {
        Assert.True(MainWindow.IsOverlayPopupInteractionSource(new ComboBoxItem()));
    }

    [Fact]
    public void OverlayPopupInteractionSourceIgnoresRegularControls()
    {
        Assert.False(MainWindow.IsOverlayPopupInteractionSource(new Button()));
    }
}
