namespace PAR.PartsGrabber;

public sealed class ContinuousRequestRunner<T>(int concurrency, TimeSpan pollInterval,
    Func<int, Task<IReadOnlyList<T>>> claim, Func<T, Task> process, Action<Exception> onError)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        if (pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var active = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                active.RemoveAll(task => task.IsCompleted);
                var slots = concurrency - active.Count;
                if (slots > 0)
                {
                    IReadOnlyList<T> items;
                    try { items = await claim(slots); }
                    catch (Exception ex) { onError(ex); await WaitAsync(active, ct); continue; }
                    if (items.Count > slots) throw new InvalidOperationException("Claim exceeded available stock processing slots.");
                    foreach (var item in items) active.Add(ProcessAsync(item));
                    if (items.Count > 0) continue;
                }
                await WaitAsync(active, ct, slots == 0);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(active); }
    }
    private async Task ProcessAsync(T item)
    {
        try { await process(item); }
        catch (Exception ex) { onError(ex); }
    }
    private async Task WaitAsync(List<Task> active, CancellationToken ct, bool full = false)
    {
        if (full) { await Task.WhenAny(active).WaitAsync(ct); return; }
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try { await Task.WhenAny(active.Append(Task.Delay(pollInterval, delay.Token))).WaitAsync(ct); }
        finally { delay.Cancel(); }
    }
}
