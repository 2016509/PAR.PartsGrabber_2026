using System.Collections.Concurrent;

using PAR.PartsGrabber;
using Xunit;

namespace PAR.PartsGrabber.Tests;

public sealed class ContinuousRequestRunnerTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(TaskCompletionSource signal) => signal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task CompletedSlotIsRefilledWhileAnotherJobIsStillBlocked(int concurrency)
    {
        using var stop = new CancellationTokenSource();
        var slow = Signal(); var fast = Signal(); var third = Signal(); var twoStarted = Signal();
        var errors = new ConcurrentQueue<Exception>(); var claims = new List<int>();
        var next = 0; var active = 0; var peak = 0;
        var runner = new ContinuousRequestRunner<TestItem>(concurrency, TimeSpan.FromHours(1), slots =>
        {
            claims.Add(slots);
            // Matches the server setting CLAIM_BATCH_SIZE=1: small claims must still fill all slots.
            return Task.FromResult<IReadOnlyList<TestItem>>(next < concurrency + 1
                ? new[] { new TestItem { Id = ++next } } : Array.Empty<TestItem>());
        }, async job =>
        {
            var count = Interlocked.Increment(ref active); peak = Math.Max(peak, count);
            if (job.Id == concurrency) twoStarted.TrySetResult();
            try
            {
                if (job.Id == 2) await fast.Task;
                else
                {
                    if (job.Id == concurrency + 1) third.SetResult();
                    await slow.Task;
                }
            }
            finally { Interlocked.Decrement(ref active); }
        }, errors.Enqueue);
        var running = runner.RunAsync(stop.Token);
        try
        {
            await Wait(twoStarted);
            Assert.Equal(Enumerable.Range(1, concurrency).Reverse(), claims);
            fast.SetResult();
            await Wait(third);
            Assert.False(slow.Task.IsCompleted);
            Assert.Equal(concurrency, peak);
            Assert.Equal(Enumerable.Range(1, concurrency).Reverse().Append(1), claims);
        }
        finally { stop.Cancel(); slow.TrySetResult(); fast.TrySetResult(); await running; }
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ProcessingErrorReleasesSlotAndLaterJobsStillRun()
    {
        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>(); var finished = Signal(); var next = 0;
        var runner = new ContinuousRequestRunner<TestItem>(1, TimeSpan.FromHours(1), _ =>
            Task.FromResult<IReadOnlyList<TestItem>>(new[] { new TestItem { Id = ++next } }),
            job =>
            {
                if (job.Id == 1) throw new InvalidOperationException("Job failed");
                finished.SetResult(); stop.Cancel(); return Task.CompletedTask;
            }, errors.Enqueue);
        await runner.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished.Task.IsCompleted); Assert.Equal(2, next); Assert.Single(errors);
    }

    [Fact]
    public async Task ShutdownStopsClaimingButDrainsAlreadyClaimedJobs()
    {
        using var stop = new CancellationTokenSource();
        var started = Signal(); var release = Signal(); var finished = false; var calls = 0;
        var runner = new ContinuousRequestRunner<TestItem>(1, TimeSpan.FromHours(1), _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<TestItem>>(new[] { new TestItem { Id = 1 } });
        }, async _ => { started.SetResult(); await release.Task; finished = true; }, _ => { });
        var running = runner.RunAsync(stop.Token);
        try
        {
            await Wait(started); stop.Cancel();
            Assert.False(running.IsCompleted); Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); await running; }
        Assert.True(finished); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ShutdownDuringClaimStillProcessesTheReturnedLease()
    {
        using var stop = new CancellationTokenSource(); var processed = 0;
        var runner = new ContinuousRequestRunner<TestItem>(2, TimeSpan.FromHours(1), _ =>
        {
            stop.Cancel();
            return Task.FromResult<IReadOnlyList<TestItem>>(new[] { new TestItem { Id = 1 } });
        }, _ => { processed++; return Task.CompletedTask; }, _ => { });
        await runner.RunAsync(stop.Token);
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task ClaimFailureDoesNotInterruptRunningJob()
    {
        using var stop = new CancellationTokenSource();
        var release = Signal(); var error = Signal(); var errors = new ConcurrentQueue<Exception>();
        var calls = 0; var finished = false;
        var runner = new ContinuousRequestRunner<TestItem>(2, TimeSpan.FromHours(1), _ =>
        {
            if (++calls == 1) return Task.FromResult<IReadOnlyList<TestItem>>(new[] { new TestItem { Id = 1 } });
            throw new HttpRequestException("Claim API unavailable");
        }, async _ => { await release.Task; finished = true; }, ex => { errors.Enqueue(ex); error.TrySetResult(); });
        var running = runner.RunAsync(stop.Token);
        try { await Wait(error); Assert.False(finished); }
        finally { stop.Cancel(); release.TrySetResult(); await running; }
        Assert.True(finished); Assert.Single(errors);
    }
}

internal sealed class TestItem { public int Id {get;set;} }
