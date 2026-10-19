using KameraData.Data.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PAR.ParseLib;

namespace PAR.PartsGrabber;

public sealed class ProcessService(IServiceScopeFactory scopes,SourceProxyProvider sources,PartsQueueClient queue,
    IOptions<ModuleOptions> options,ModuleMetrics metrics,ILogger logger)
{
    private List<CheckProxyResult>? snapshot;
    private DateTime refreshAt;
    private sealed record Work(PartsAndReplace Part,List<CheckProxyResult> Sources);
    public async Task RunAsync(CancellationToken ct=default)
    {
        var runner=new ContinuousRequestRunner<Work>(Math.Max(1,options.Value.MaxConcurrentParts),
            TimeSpan.FromMilliseconds(Math.Max(100,options.Value.PollIntervalMs)),ClaimAsync,async work=>{
                using var scope=scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PartProcessor>().ProcessAsync(work.Part,work.Sources);
            },ex=>{metrics.ReportApiUnavailable();metrics.ReportError("admission_error");logger.LogError(ex,"PartsGrabber admission or processing failed");});
        await runner.RunAsync(ct);
    }
    private async Task<IReadOnlyList<Work>> ClaimAsync(int slots)
    {
        if(snapshot==null||DateTime.UtcNow>=refreshAt) {
            var loaded=await sources.GetActiveSourceProxiesAsync();
            if(loaded.Count==0)throw new InvalidOperationException("No source/proxy bindings loaded; no parts claimed.");
            snapshot=loaded;refreshAt=DateTime.UtcNow.AddSeconds(Math.Max(1,options.Value.SourceRefreshSeconds));
        }
        var parts=await queue.ClaimAsync(slots);metrics.ReportApiAvailable();
        return parts.Select(part=>new Work(part,JsonConvert.DeserializeObject<List<CheckProxyResult>>(
            JsonConvert.SerializeObject(snapshot,new JsonSerializerSettings{ReferenceLoopHandling=ReferenceLoopHandling.Ignore}))!)).ToArray();
    }
}
