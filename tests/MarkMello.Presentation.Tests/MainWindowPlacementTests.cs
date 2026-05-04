using Avalonia;
using MarkMello.Domain;
using MarkMello.Presentation.Views;

namespace MarkMello.Presentation.Tests;

public sealed class MainWindowPlacementTests
{
    [Fact]
    public void CalculateStartupWindowPlacementCentersDefaultWindowInsideWorkingArea()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1040);

        var placement = MainWindow.CalculateStartupWindowPlacement(
            savedPlacement: null,
            workingArea,
            screenScaling: 1,
            minWidth: 640,
            minHeight: 480);

        Assert.Equal(1280d, placement.Width);
        Assert.Equal(840d, placement.Height);
        Assert.True(placement.X >= 0);
        Assert.True(placement.Y >= 0);
        Assert.True(placement.X + placement.Width <= workingArea.Width);
        Assert.True(placement.Y + placement.Height <= workingArea.Height);
    }

    [Fact]
    public void CalculateStartupWindowPlacementClampsSavedWindowInsideWorkingArea()
    {
        var workingArea = new PixelRect(0, 0, 1280, 720);
        var savedPlacement = new WindowPlacement(-200, -100, 1600, 1200, IsMaximized: false);

        var placement = MainWindow.CalculateStartupWindowPlacement(
            savedPlacement,
            workingArea,
            screenScaling: 1,
            minWidth: 640,
            minHeight: 480);

        Assert.Equal(1264d, placement.Width);
        Assert.Equal(704d, placement.Height);
        Assert.Equal(8d, placement.X);
        Assert.Equal(8d, placement.Y);
    }

    [Fact]
    public void CalculateStartupWindowPlacementUsesScreenScalingForPixelBounds()
    {
        var workingArea = new PixelRect(0, 0, 2880, 1800);
        var savedPlacement = new WindowPlacement(2600, 1700, 1200, 800, IsMaximized: false);

        var placement = MainWindow.CalculateStartupWindowPlacement(
            savedPlacement,
            workingArea,
            screenScaling: 2,
            minWidth: 640,
            minHeight: 480);

        Assert.Equal(472d, placement.X);
        Assert.Equal(192d, placement.Y);
        Assert.Equal(1200d, placement.Width);
        Assert.Equal(800d, placement.Height);
    }
}
