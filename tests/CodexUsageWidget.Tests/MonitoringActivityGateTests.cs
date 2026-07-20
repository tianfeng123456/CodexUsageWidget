using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class MonitoringActivityGateTests
{
    [Fact]
    public void Pause_IsIdempotent_AndCancelsActiveLease()
    {
        using var gate = new MonitoringActivityGate();
        Assert.True(gate.TryCapture(out var active));

        Assert.True(gate.Pause());
        Assert.False(gate.Pause());

        Assert.True(gate.IsPaused);
        Assert.True(active.CancellationToken.IsCancellationRequested);
        Assert.False(gate.IsCurrent(active));
        Assert.False(gate.TryCapture(out _));
    }

    [Fact]
    public void Resume_CreatesNewGeneration_AndIsIdempotent()
    {
        using var gate = new MonitoringActivityGate();
        Assert.True(gate.TryCapture(out var first));
        Assert.True(gate.Pause());

        Assert.True(gate.Resume());
        Assert.False(gate.Resume());
        Assert.True(gate.TryCapture(out var second));

        Assert.False(gate.IsPaused);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
        Assert.False(second.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CapturedLease_CanStillBeLinkedAfterPauseRace()
    {
        using var gate = new MonitoringActivityGate();
        Assert.True(gate.TryCapture(out var active));

        Assert.True(gate.Pause());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            active.CancellationToken);

        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public void Pause_CancellationCallbackCanQueryGate_WithoutDeadlock()
    {
        using var gate = new MonitoringActivityGate();
        Assert.True(gate.TryCapture(out var active));
        var callbackCompleted = false;
        using var registration = active.CancellationToken.Register(
            () => callbackCompleted = !gate.IsCurrent(active));

        Assert.True(gate.Pause());

        Assert.True(callbackCompleted);
    }

    [Fact]
    public void Dispose_CancelsLease_AndPreventsFutureTransitions()
    {
        var gate = new MonitoringActivityGate();
        Assert.True(gate.TryCapture(out var active));

        gate.Dispose();
        gate.Dispose();

        Assert.True(gate.IsPaused);
        Assert.True(active.CancellationToken.IsCancellationRequested);
        Assert.False(gate.TryCapture(out _));
        Assert.False(gate.Resume());
    }

    [Fact]
    public void CompletedDormantGenerations_CanBeReleasedAcrossRepeatedCycles()
    {
        using var gate = new MonitoringActivityGate();

        for (var cycle = 0; cycle < 100; cycle++)
        {
            Assert.True(gate.TryCapture(out var active));
            Assert.True(gate.Pause());
            Assert.True(active.CancellationToken.IsCancellationRequested);

            gate.ReleaseRetiredCancellations();

            Assert.True(gate.Resume());
        }

        Assert.True(gate.TryCapture(out var current));
        Assert.True(gate.IsCurrent(current));
    }
}
