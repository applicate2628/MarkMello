using MarkMello.Presentation.Views.Markdown.Minimap;

namespace MarkMello.Presentation.Tests;

public sealed class DocumentMiniatureSnapshotTests
{
    [Fact]
    public void EmptySnapshotIsEmpty()
    {
        Assert.True(DocumentMiniatureSnapshot.Empty.IsEmpty);
    }

    [Fact]
    public void ConstructorNormalizesNegativeDimensions()
    {
        var snapshot = new DocumentMiniatureSnapshot(-10, -20);

        Assert.Equal(0, snapshot.TotalWidth);
        Assert.Equal(0, snapshot.TotalHeight);
        Assert.True(snapshot.IsEmpty);
    }

    [Fact]
    public void SnapshotWithPositiveDimensionsIsNotEmpty()
    {
        var snapshot = new DocumentMiniatureSnapshot(800, 2_400);

        Assert.Equal(800, snapshot.TotalWidth);
        Assert.Equal(2_400, snapshot.TotalHeight);
        Assert.False(snapshot.IsEmpty);
    }
}
