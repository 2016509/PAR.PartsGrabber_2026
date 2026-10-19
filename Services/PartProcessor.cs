using System.Diagnostics;
using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;

namespace PAR.PartsGrabber;

public sealed class PartProcessor(ProcessParsingResultService saver,IOptions<ModuleOptions> options,
    IOptions<ApiServiceOptions> apiOptions,IApiService api,IParseService parser,ITelegramNotificationService telegram,
    ModuleMetrics metrics,SharedProcessingLimits limits,PartsQueueClient queue,ILogger logger)
{
    public async Task ProcessAsync(PartsAndReplace part,List<CheckProxyResult> sources)
    {
        await using var lease=PartProcessingLease.Begin(queue,part,options.Value,logger);
        var timer=Stopwatch.StartNew();metrics.ActiveParts.Inc();
        try {
            await lease.StartAsync();
            if(string.IsNullOrWhiteSpace(part.MainPartNumber))throw new InvalidOperationException("Part number is empty.");
            logger.LogInformation("Part processing started: Id={Id}, Part={Part}, Attempt={Attempt}",part.Id,part.MainPartNumber,part.PartsAttemptCount);
            using var parseTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(1));
            List<ParsingPart> results;
            try{results=await parser.Parse(part.MainPartNumber,sources,parseTimeout.Token);}
            catch(OperationCanceledException ex) when(parseTimeout.IsCancellationRequested)
            {throw new TimeoutException("Parsing timed out before returning results; retry required.",ex);}
            var timedOut=parseTimeout.IsCancellationRequested;
            if(timedOut) {
                await telegram.SendTimeoutNotificationAsync("Timeout after 1 min for part "+part.MainPartNumber+". Saving partial results.",DateTime.UtcNow-timer.Elapsed);
                foreach(var result in results) {
                    result.AttempsCount++;
                    if(string.IsNullOrEmpty(result.Name)&&!result.Replaces.Any()&&!result.ParsingPictures.Any()&&string.IsNullOrEmpty(result.SitePartNumber)&&result.RegularPrice<=0)
                        result.WithErrorToSave=true;
                }
            }
            // Saving gets its own timeout budget after parsing, rather than an already expired token.
            using var saveTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1,options.Value.ApiRetryIntervalSeconds)));
            await Task.WhenAll(results.Select(async result=>{
                await limits.Saves.WaitAsync(saveTimeout.Token);
                try {
                    if(result.WithErrorToSave)await api.Post(apiOptions.Value.BaseUrl+apiOptions.Value.SaveErrorUrl,
                        new ErrorLog{error_message=$"Parsing failed for site {result.PartSource.SourceName}{(timedOut?" (timeout)":"")}. Source status is managed by PartsSourceHealthChecker.",script_name="ParsGrabber"},saveTimeout.Token);
                    else await saver.Save(result,part,saveTimeout.Token);
                }finally{limits.Saves.Release();}
            }));
            await saver.UpdatePartsAndReplace(results,part,sources.Select(x=>x.PartSource).ToList(),saveTimeout.Token);
            metrics.ReportPartProcessed(true);
            logger.LogInformation("Part processing completed: Id={Id}, DurationMs={Ms}",part.Id,timer.ElapsedMilliseconds);
        }
        catch(Exception ex) {
            metrics.ReportPartProcessed(false);metrics.ReportError("processing_error");logger.LogError(ex,"Part processing failed: Id={Id}",part.Id);
            try{await lease.FailAsync(ex);}catch(Exception failure){logger.LogError(failure,"Cannot save part failure: Id={Id}",part.Id);}
        }
        finally{metrics.ActiveParts.Dec();metrics.ReportProcessingDuration(timer.Elapsed.TotalSeconds);metrics.UpdateUptime();}
    }
}
