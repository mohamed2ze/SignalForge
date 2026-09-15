using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Worker;
using SignalForge.Worker.Services;
using WorkerHost = SignalForge.Worker.Worker;

namespace SignalForge.UnitTests.Services;

/// <summary>
/// Deterministic tests for the graceful-drain shutdown contract shared by the outbox loop
/// (<see cref="Worker"/>) and the execution pump loop
/// (<see cref="WorkflowExecutionPumpHostedService"/>): when the host begins shutting down, the
/// loop must stop accepting new cycles immediately but must let an in-flight cycle finish its
/// current work — instead of cancelling it the instant shutdown begins — up to the configured
/// drain window, which also bounds genuinely stuck cycles.
/// </summary>
public class GracefulShutdownTests
{
    /// <summary>
    /// A loop target whose cycle can be parked on a gate, records the token it was given, and
    /// counts invocations so tests can prove no new cycles begin after shutdown.
    /// </summary>
    private sealed class BlockingProcessor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastToken { get; private set; }
        public int CallCount { get; private set; }
        public bool Block { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(25);

        public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            LastToken = cancellationToken;

            if (!Block)
                return Delay;

            Entered.TrySetResult();
            // Park until released — or until the drain token cancels (a stuck cycle).
            await Release.Task.WaitAsync(cancellationToken);
            return Delay;
        }
    }

    private sealed class BlockingOutboxProcessor(BlockingProcessor inner) : IOutboxProcessor
    {
        public Task<TimeSpan> ProcessBatchAsync(CancellationToken cancellationToken)
            => inner.StepAsync(cancellationToken);
    }

    private sealed class BlockingPump(BlockingProcessor inner) : IWorkflowExecutionPump
    {
        public Task<TimeSpan> ProcessCycleAsync(CancellationToken cancellationToken)
            => inner.StepAsync(cancellationToken);
    }

    private static ServiceProvider BuildProvider(IOutboxProcessor processor)
    {
        var services = new ServiceCollection();
        services.AddSingleton(processor);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider(IWorkflowExecutionPump pump)
    {
        var services = new ServiceCollection();
        services.AddSingleton(pump);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task OutboxLoop_LetsInFlightCycleFinishAfterShutdown()
    {
        var events = new BlockingProcessor { Block = true };
        var worker = new WorkerHost(
            BuildProvider(new BlockingOutboxProcessor(events)),
            Options.Create(new HostingOptions { GracefulShutdownTimeoutSeconds = 5 }),
            NullLogger<WorkerHost>.Instance);

        var run = worker.StartAsync(CancellationToken.None);
        await events.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Host begins shutdown while the cycle is in flight.
        var stopping = worker.StopAsync(CancellationToken.None);

        // The drain window is generous, so the in-flight cycle's token must NOT be cancelled
        // the moment shutdown begins — the work is allowed to complete.
        await Task.Delay(300);
        Assert.False(events.LastToken.IsCancellationRequested);

        // The cycle finishes its work; the loop then exits without starting any new cycle.
        events.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));

        var callsAfterDrain = events.CallCount;
        await Task.Delay(150);
        Assert.Equal(callsAfterDrain, events.CallCount);
    }

    [Fact]
    public async Task OutboxLoop_StopsAcceptingNewCyclesImmediatelyOnShutdown()
    {
        var events = new BlockingProcessor { Block = false };
        var worker = new WorkerHost(
            BuildProvider(new BlockingOutboxProcessor(events)),
            Options.Create(new HostingOptions { GracefulShutdownTimeoutSeconds = 5 }),
            NullLogger<WorkerHost>.Instance);

        var run = worker.StartAsync(CancellationToken.None);

        // Let a couple of healthy cycles run, then shut down.
        await Task.Delay(150);
        var callsBeforeShutdown = events.CallCount;
        Assert.True(callsBeforeShutdown > 0, "expected at least one healthy cycle");

        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // The loop gate is checked before each cycle: shutdown means no further polls.
        var callsAtShutdown = events.CallCount;
        await Task.Delay(150);
        Assert.Equal(callsAtShutdown, events.CallCount);

        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OutboxLoop_DrainWindowCancelsStuckCycle()
    {
        var events = new BlockingProcessor { Block = true };
        var worker = new WorkerHost(
            BuildProvider(new BlockingOutboxProcessor(events)),
            Options.Create(new HostingOptions { GracefulShutdownTimeoutSeconds = 0.05 }),
            NullLogger<WorkerHost>.Instance);

        var run = worker.StartAsync(CancellationToken.None);
        await events.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = worker.StopAsync(CancellationToken.None);

        // The drain window elapses, the stuck cycle is hard-cancelled, and the loop exits.
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(events.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ExecutionPumpLoop_LetsInFlightCycleFinishAfterShutdown()
    {
        var events = new BlockingProcessor { Block = true };
        var host = new WorkflowExecutionPumpHostedService(
            BuildProvider(new BlockingPump(events)),
            Options.Create(new HostingOptions { GracefulShutdownTimeoutSeconds = 5 }),
            NullLogger<WorkflowExecutionPumpHostedService>.Instance);

        var run = host.StartAsync(CancellationToken.None);
        await events.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = host.StopAsync(CancellationToken.None);

        await Task.Delay(300);
        Assert.False(events.LastToken.IsCancellationRequested);

        events.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
    }
}