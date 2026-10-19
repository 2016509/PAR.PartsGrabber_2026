using KameraData.Data.Models;
using Microsoft.Extensions.Logging;

namespace PAR.PartsGrabber;

public sealed class PartProcessingLease:IAsyncDisposable
{
    private static readonly AsyncLocal<PartProcessingLease?> Ambient=new();
    public static PartProcessingLease? Current=>Ambient.Value;
    private readonly PartProcessingLease? previous;
    private readonly PartsQueueClient queue;
    private readonly ModuleOptions config;
    private readonly ILogger logger;
    private readonly PartsAndReplace part;
    private readonly CancellationTokenSource stop=new();
    private Task? heartbeat;
    private volatile bool lost;
    private bool finished;
    private PartProcessingLease(PartsQueueClient queue,PartsAndReplace part,ModuleOptions config,ILogger logger)
    {this.queue=queue;this.part=part;this.config=config;this.logger=logger;previous=Ambient.Value;}
    public static PartProcessingLease Begin(PartsQueueClient queue,PartsAndReplace part,ModuleOptions config,ILogger logger)
    {
        if(part.Id<=0||string.IsNullOrWhiteSpace(part.PartsWorkerId)||part.PartsAttemptCount<=0||part.Status!="1")
            throw new InvalidOperationException("Invalid PartsGrabber claim.");
        var lease=new PartProcessingLease(queue,part,config,logger);Ambient.Value=lease;return lease;
    }
    private async Task SendAsync(string operation,object? data=null,string? error=null,CancellationToken ct=default)
    {
        if(lost||finished)throw new InvalidOperationException("Part lease no longer owned.");
        try{await queue.ExecuteAsync(part,operation,data,error,ct);}
        catch(HttpRequestException ex) when(ex.StatusCode==System.Net.HttpStatusCode.Conflict)
        {lost=true;throw new InvalidOperationException("Part lease expired or ownership changed.",ex);}
    }
    public async Task StartAsync(){await SendAsync("renew");heartbeat=HeartbeatAsync();}
    private async Task HeartbeatAsync()
    {
        try{while(true){await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(config.LeaseSeconds/3,10,30)),stop.Token);await SendAsync("renew");}}
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        catch(Exception ex){lost=true;logger.LogError(ex,"Part lease heartbeat failed: {Id}",part.Id);}
    }
    private async Task StopAsync(){stop.Cancel();if(heartbeat!=null)await heartbeat;}
    public async Task WriteAsync(string operation,object data,CancellationToken ct=default)
    {
        if(operation!="complete"){await SendAsync(operation,data,ct:ct);return;}
        await StopAsync();
        try{await SendAsync(operation,data,ct:ct);}catch(HttpRequestException){await SendAsync(operation,data,ct:ct);}
        finished=true;
    }
    public async Task FailAsync(Exception ex)
    {await StopAsync();if(lost||finished)return;await SendAsync("fail",error:ex.ToString());finished=true;}
    public async ValueTask DisposeAsync(){await StopAsync();lost=true;stop.Dispose();Ambient.Value=previous;}
}
