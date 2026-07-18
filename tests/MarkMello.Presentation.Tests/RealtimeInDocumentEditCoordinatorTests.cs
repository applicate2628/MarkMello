using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

public sealed class RealtimeInDocumentEditCoordinatorTests
{
    [Fact]
    public async Task SecondEditWhileFirstIsInFlightIsRefusedBusyAndDoesNotApply()
    {
        // The unified in-flight guard (replacing the old independent
        // _isTogglingTask / _isEditingCell flags) serializes realtime edits: a
        // second edit arriving while the first still holds the serializer is told
        // busy and never runs. Exercised directly on the coordinator so the guard
        // stays covered even though P1's reading/edit legs are synchronous.
        var coordinator = new RealtimeInDocumentEditCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new GatedEditKind(gate.Task);
        var second = new GatedEditKind(Task.CompletedTask);

        var firstRun = coordinator.ApplyAsync(first);
        await first.Started.Task; // the first edit is inside the serializer, awaiting the gate

        await coordinator.ApplyAsync(second); // serializer busy -> refused

        Assert.True(second.BusyPublished);
        Assert.False(second.Applied);

        gate.SetResult();
        await firstRun;
        Assert.True(first.Applied);
        Assert.False(first.BusyPublished);
    }

    [Fact]
    public async Task SerializerIsReleasedAfterAnEditKindThrows()
    {
        // A throwing edit must not wedge the serializer (the coordinator's finally
        // clears the in-flight flag) — a later edit still runs.
        var coordinator = new RealtimeInDocumentEditCoordinator();
        var throwing = new ThrowingEditKind();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(throwing));

        var next = new GatedEditKind(Task.CompletedTask);
        await coordinator.ApplyAsync(next);

        Assert.True(next.Applied);
        Assert.False(next.BusyPublished);
    }

    private sealed class GatedEditKind(Task gate) : IInDocumentEditKind
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Applied { get; private set; }

        public bool BusyPublished { get; private set; }

        public async Task ApplyAsync()
        {
            Started.TrySetResult();
            await gate;
            Applied = true;
        }

        public void PublishBusy() => BusyPublished = true;
    }

    private sealed class ThrowingEditKind : IInDocumentEditKind
    {
        public Task ApplyAsync() => throw new InvalidOperationException("boom");

        public void PublishBusy()
        {
        }
    }
}
