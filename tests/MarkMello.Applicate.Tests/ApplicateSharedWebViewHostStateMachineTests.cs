using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Tests for the pure slot-visibility state machine that backs
/// <see cref="ApplicateSharedWebViewHost"/>. Anchored to design D1 / D10:
/// state transitions, generation monotonicity, mode-toggle atomicity.
/// </summary>
public sealed class ApplicateSharedWebViewHostStateMachineTests
{
    private static readonly string HostSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateSharedWebViewHost.cs");

    [Fact]
    public void NewMachineStartsParked()
    {
        var sm = new ApplicateSharedWebViewHostStateMachine();

        Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Parked, sm.CurrentState);
        Assert.Equal(0, sm.CurrentGeneration);
        Assert.Null(sm.CurrentParent);
    }

    [Fact]
    public void SetWarmupParentTransitionsToParkedAndAdoptsPanel()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            sm.SetWarmupParent(warmup);

            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Parked, sm.CurrentState);
            Assert.Same(warmup, sm.CurrentParent);
        }, CancellationToken.None);
    }

    [Fact]
    public void AttachToTransitionsToSwitchingAndHidesTargetSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);

            sm.AttachTo(slot);

            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Switching, sm.CurrentState);
            Assert.Same(slot, sm.CurrentParent);
            Assert.False(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void AttachToKeepsTargetTransparentUntilDocumentRendered()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var viewer = new Panel { IsVisible = true, Opacity = 1.0 };
            var edit = new Panel { IsVisible = true, Opacity = 1.0 };
            sm.SetWarmupParent(warmup);

            sm.AttachTo(viewer);
            Assert.Equal(0.0, viewer.Opacity);
            Assert.True(sm.ApplyDocumentRendered(sm.RequestRender()));
            Assert.Equal(1.0, viewer.Opacity);

            sm.AttachTo(edit);

            Assert.Equal(0.0, edit.Opacity);
            Assert.True(sm.ApplyDocumentRendered(sm.RequestRender()));
            Assert.Equal(1.0, edit.Opacity);
        }, CancellationToken.None);
    }

    [Fact]
    public void RequestRenderBumpsGenerationAndHidesActiveSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            // Manually pretend the prior generation committed.
            sm.ApplyDocumentRendered(sm.RequestRender());

            slot.IsVisible = true;

            var gen = sm.RequestRender();

            Assert.True(gen > 1);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Switching, sm.CurrentState);
            Assert.False(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void ApplyDocumentRenderedCommitsAndShowsSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            var gen = sm.RequestRender();

            var committed = sm.ApplyDocumentRendered(gen);

            Assert.True(committed);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Committed, sm.CurrentState);
            Assert.True(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void StaleDocumentRenderedIsDropped()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            var firstGen = sm.RequestRender();
            var secondGen = sm.RequestRender();

            Assert.NotEqual(firstGen, secondGen);

            // Stale event from earlier generation must be silently dropped —
            // state stays Switching, slot stays hidden (Invariant I-4).
            var committed = sm.ApplyDocumentRendered(firstGen);

            Assert.False(committed);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Switching, sm.CurrentState);
            Assert.False(slot.IsVisible);

            // The later generation still commits cleanly.
            Assert.True(sm.ApplyDocumentRendered(secondGen));
            Assert.True(slot.IsVisible);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Committed, sm.CurrentState);
        }, CancellationToken.None);
    }

    [Fact]
    public void ModeToggleNeverShowsBothSlotsSimultaneously()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var viewer = new Panel { IsVisible = true };
            var edit = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);

            sm.AttachTo(viewer);
            var gen1 = sm.RequestRender();
            sm.ApplyDocumentRendered(gen1);
            Assert.True(viewer.IsVisible);

            // Mode toggle: re-attach to the edit slot. Invariant I-6: no
            // moment exists when both slots are IsVisible=true.
            sm.AttachTo(edit);

            Assert.False(edit.IsVisible);
            // Viewer slot has had its visibility restored — but only as a
            // layout-host invariant, NOT because it currently displays the
            // WebView. The state machine itself never "shows both".
            // The actual WebView lives in edit now.
            Assert.Same(edit, sm.CurrentParent);
        }, CancellationToken.None);
    }

    [Fact]
    public void GenerationMonotonicallyIncreases()
    {
        var sm = new ApplicateSharedWebViewHostStateMachine();
        var firstGen = sm.RequestRender();
        var secondGen = sm.RequestRender();
        var thirdGen = sm.RequestRender();

        Assert.True(secondGen > firstGen);
        Assert.True(thirdGen > secondGen);
    }

    [Fact]
    public void ReturnToWarmupClearsConsumerSlotAndRestoresVisibility()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            var gen = sm.RequestRender();
            sm.ApplyDocumentRendered(gen);

            sm.ReturnToWarmup();

            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Parked, sm.CurrentState);
            Assert.Same(warmup, sm.CurrentParent);
            Assert.True(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void SharedHostOwnsRevealFadeAfterRendererSettle()
    {
        var source = File.ReadAllText(HostSourcePath);
        var completeReveal = ExtractMethodBody(
            source,
            source.IndexOf("private void CompleteReveal()", StringComparison.Ordinal));

        Assert.Contains("PrepareTargetForReveal", source, StringComparison.Ordinal);
        Assert.Contains("RevealCurrentParent", source, StringComparison.Ordinal);
        Assert.Contains("ApplicateMotion.ModeSwitchDuration", source, StringComparison.Ordinal);
        Assert.Contains("CompleteNativeWebViewHiddenPaint", completeReveal, StringComparison.Ordinal);
        Assert.True(
            completeReveal.IndexOf("RevealCurrentParent", StringComparison.Ordinal)
            > completeReveal.IndexOf("View.CompleteNativeWebViewHiddenPaint", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedHostFadesRendererDocumentInsideNativeWebView()
    {
        var source = File.ReadAllText(HostSourcePath);
        var completeReveal = ExtractMethodBody(
            source,
            source.IndexOf("private void CompleteReveal()", StringComparison.Ordinal));

        Assert.Contains("PrepareNativeRendererForReveal", source, StringComparison.Ordinal);
        Assert.Contains("PrepareNativeWebViewForHiddenPaint", source, StringComparison.Ordinal);
        Assert.Contains("CompleteNativeWebViewHiddenPaint", source, StringComparison.Ordinal);
        Assert.Contains("RevealNativeRenderer", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("View.PrepareNativeRendererForReveal", StringComparison.Ordinal)
            < source.IndexOf("View.PrepareNativeWebViewForHiddenPaint", StringComparison.Ordinal));
        Assert.True(
            source.IndexOf("View.PrepareNativeWebViewForHiddenPaint", StringComparison.Ordinal)
            < source.IndexOf("BeginRevealAfterSettle", StringComparison.Ordinal));
        Assert.True(
            completeReveal.IndexOf("View.RevealNativeRenderer", StringComparison.Ordinal)
            > completeReveal.IndexOf("View.CompleteNativeWebViewHiddenPaint", StringComparison.Ordinal));
    }

    [Fact]
    public void RevealGateRehidesNativeWindowAfterLayoutBeforeOffscreenPrepaint()
    {
        var source = File.ReadAllText(HostSourcePath);
        var commit = ExtractMethodBody(
            source,
            source.IndexOf("private void Commit()", StringComparison.Ordinal));

        Assert.Contains("View.SetNativeWebViewVisibility(false);", commit, StringComparison.Ordinal);
        Assert.True(
            commit.IndexOf("_currentParent.UpdateLayout();", StringComparison.Ordinal)
            < commit.IndexOf("View.SetNativeWebViewVisibility(false);", StringComparison.Ordinal));
        Assert.True(
            commit.IndexOf("View.SetNativeWebViewVisibility(false);", StringComparison.Ordinal)
            < commit.IndexOf("View.PrepareNativeWebViewForHiddenPaint();", StringComparison.Ordinal));
    }

    [Fact]
    public void AttachToParksNativeWindowOffscreenBeforeReparent()
    {
        var source = File.ReadAllText(HostSourcePath);
        var attachTo = ExtractMethodBody(
            source,
            source.IndexOf("public void AttachTo(Panel target, ApplicateWebMountIntent intent)", StringComparison.Ordinal));

        Assert.Contains("View.ParkNativeWebViewForReparent();", attachTo, StringComparison.Ordinal);
        Assert.True(
            attachTo.IndexOf("View.ParkNativeWebViewForReparent();", StringComparison.Ordinal)
            < attachTo.IndexOf("using (View.BeginIntentionalReparent())", StringComparison.Ordinal));
    }

    [Fact]
    public void AttachToRehidesNativeWindowAfterTargetVisibilityReturns()
    {
        var source = File.ReadAllText(HostSourcePath);
        var attachTo = ExtractMethodBody(
            source,
            source.IndexOf("public void AttachTo(Panel target, ApplicateWebMountIntent intent)", StringComparison.Ordinal));
        var visibilityRestore = attachTo.IndexOf("target.IsVisible = _hasEverCommitted;", StringComparison.Ordinal);
        var postRestoreHide = attachTo.IndexOf("View.SetNativeWebViewVisibility(false);", visibilityRestore, StringComparison.Ordinal);

        Assert.True(visibilityRestore >= 0);
        Assert.True(postRestoreHide > visibilityRestore);
    }

    [Fact]
    public void RevealGateFallbackStaysBehindRendererPostChromeSettleBudget()
    {
        var source = File.ReadAllText(HostSourcePath);

        Assert.Contains("TimeSpan.FromMilliseconds(500)", source, StringComparison.Ordinal);
        Assert.Contains("post-chrome", source, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string source, int methodStart)
    {
        Assert.True(methodStart >= 0);

        var bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0);

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException("Could not find method body.");
    }
}
