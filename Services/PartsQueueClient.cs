using System.Net.Http.Json;
using KameraData.Data.Models;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace PAR.PartsGrabber;

public class PartsQueueClient(HttpClient http,IOptions<ApiServiceOptions> api,IOptions<ModuleOptions> settings)
{
    private string Root=>api.Value.PartsRoot+"/PartsAndReplaces/continuous";
    public virtual async Task<List<PartsAndReplace>> ClaimAsync(int slots)
    {
        var config=settings.Value;
        var owner=config.WorkerId[..Math.Min(90,config.WorkerId.Length)]+":"+Guid.NewGuid().ToString("N");
        using var response=await http.PostAsJsonAsync(Root+"/claim",new{WorkerId=owner,
            BatchSize=Math.Min(slots,Math.Clamp(config.ClaimBatchSize,1,100)),config.LeaseSeconds,config.MaxAttempts});
        response.EnsureSuccessStatusCode();
        var json=JToken.Parse(await response.Content.ReadAsStringAsync());
        return (json.Type==JTokenType.Object?json["$values"]:json)?.ToObject<List<PartsAndReplace>>()??new();
    }
    public virtual async Task ExecuteAsync(PartsAndReplace part,string operation,object? data=null,string? error=null,CancellationToken ct=default)
    {
        var config=settings.Value;
        using var response=await http.PostAsJsonAsync($"{Root}/{part.Id}/{operation}",new{WorkerId=part.PartsWorkerId,
            AttemptCount=part.PartsAttemptCount,config.LeaseSeconds,config.MaxAttempts,config.RetryDelaySeconds,Data=data,Error=error},ct);
        response.EnsureSuccessStatusCode();
    }
}
