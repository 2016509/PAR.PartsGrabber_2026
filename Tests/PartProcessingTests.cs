using System.Collections.Concurrent;
using System.Net;
using KameraData.Data.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using PAR.ParseLib;
using Xunit;

namespace PAR.PartsGrabber.Tests;

public sealed class PartProcessingTests
{
    [Fact]
    public async Task CoordinatorRefillsSlotsWithFreshProcessorsAndIndependentSourceSnapshots()
    {
        using var f=new Fixture();using var stop=new CancellationTokenSource();var slow=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Queue.Next=()=>f.Part(Interlocked.Increment(ref f.Queue.Claimed));
        var instances=0;
        var services=new ServiceCollection();
        services.AddTransient(_=>{
            Interlocked.Increment(ref instances);
            return f.Processor(new Parser(async part=>{if(part=="PART1")await slow.Task;return part;},sources=>{
                Assert.Equal("test.site",sources[0].PartSource.SourceName);sources[0].PartSource.SourceName="mutated";
            }));
        });
        using var provider=services.BuildServiceProvider();var source=new SourcesProvider();
        var runner=new ProcessService(provider.GetRequiredService<IServiceScopeFactory>(),source,f.Queue,
            Microsoft.Extensions.Options.Options.Create(new ModuleOptions{MaxConcurrentParts=2}),new ModuleMetrics(NullLogger<ModuleMetrics>.Instance),NullLogger.Instance);
        var run=runner.RunAsync(stop.Token);
        await f.Queue.ThirdDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(slow.Task.IsCompleted);Assert.Equal(3,instances);Assert.Equal(1,source.Calls);
        stop.Cancel();slow.SetResult();await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3,f.Queue.Results.Count);
    }
    [Fact]
    public async Task ParallelPartsKeepOwnersAndResultsSeparate()
    {
        using var f=new Fixture();var slow=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=f.Processor(new Parser(async part=>{await slow.Task;return part;})).ProcessAsync(f.Part(1),f.Sources());
        await f.Processor(new Parser(part=>Task.FromResult(part))).ProcessAsync(f.Part(2),f.Sources());
        Assert.Contains((2,"complete"),f.Queue.Calls);Assert.DoesNotContain((1,"complete"),f.Queue.Calls);
        slow.SetResult();await first;
        Assert.Equal("NamePART1",f.Queue.Results[1].PartName);Assert.Equal("NamePART2",f.Queue.Results[2].PartName);
    }

    [Fact]
    public async Task SavingHasFreshTimeoutBudgetAfterParsing()
    {
        using var f=new Fixture(1);
        await f.Processor(new Parser(async part=>{await Task.Delay(1100);return part;})).ProcessAsync(f.Part(2),f.Sources());
        Assert.Contains((2,"complete"),f.Queue.Calls);Assert.DoesNotContain((2,"fail"),f.Queue.Calls);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("cancel")]
    [InlineData("parse")]
    public async Task FailureOrCancellationDoesNotCompletePart(string stage)
    {
        using var f=new Fixture();f.Queue.Failure=stage;
        await f.Processor(new Parser(part=>stage=="parse"?Task.FromException<string>(new OperationCanceledException()):Task.FromResult(part)))
            .ProcessAsync(f.Part(1),f.Sources());
        Assert.Contains((1,"fail"),f.Queue.Calls);Assert.DoesNotContain((1,"complete"),f.Queue.Calls);
    }

    [Fact]
    public async Task LostLeaseStopsBeforeParsing()
    {
        using var f=new Fixture();f.Queue.Failure="lease";var invoked=false;
        await f.Processor(new Parser(part=>{invoked=true;return Task.FromResult(part);})).ProcessAsync(f.Part(1),f.Sources());
        Assert.False(invoked);Assert.Equal(new[]{(1,"renew")},f.Queue.Calls.ToArray());
    }

    private sealed class Fixture:IDisposable
    {
        private readonly HttpClient http=new(new NoNetwork());
        private readonly ProxiedHttpClientPool pool=new();
        private readonly PlaywrightFetcher pw=new(NullLogger<PlaywrightFetcher>.Instance);
        public FakeQueue Queue;
        private readonly ModuleOptions config;
        private readonly ApiServiceOptions api=new(){BaseUrl="http://api.test/api",SaveErrorUrl="/error"};
        private readonly ModuleMetrics metrics=new(NullLogger<ModuleMetrics>.Instance);
        private readonly SharedProcessingLimits limits;
        public Fixture(int saveSeconds=30)
        {config=new(){ApiRetryIntervalSeconds=saveSeconds};Queue=new(http,api,config);limits=new(Microsoft.Extensions.Options.Options.Create(config));}
        public PartsAndReplace Part(int id)=>new(){Id=id,MainPartNumber="PART"+id,Status="1",PartsWorkerId="owner-"+id,PartsAttemptCount=1};
        public List<CheckProxyResult> Sources()=>new(){new(){PartSource=new PartSource{Id=1,SourceName="test.site",Link="https://test.site/"}}};
        public PartProcessor Processor(IParseService parser)
        {
            var apiService=new LeaseAwareApiService(new ApiService(http,NullLogger.Instance));
            var saver=new ProcessParsingResultService(apiService,Microsoft.Extensions.Options.Options.Create(api),http,pw,pool,NullLogger.Instance);
            return new(saver,Microsoft.Extensions.Options.Options.Create(config),Microsoft.Extensions.Options.Options.Create(api),apiService,parser,new Notifications(),metrics,limits,Queue,NullLogger.Instance);
        }
        public void Dispose(){http.Dispose();pool.Dispose();}
    }
    private sealed class FakeQueue(HttpClient http,ApiServiceOptions api,ModuleOptions config):PartsQueueClient(http,
        Microsoft.Extensions.Options.Options.Create(api),Microsoft.Extensions.Options.Options.Create(config))
    {
        public ConcurrentQueue<(int,string)> Calls=new();
        public ConcurrentDictionary<int,PartsAndReplace> Results=new();
        public string? Failure;
        public int Claimed;
        public Func<PartsAndReplace>? Next;
        public TaskCompletionSource ThirdDone=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<List<PartsAndReplace>> ClaimAsync(int slots)
        {
            Assert.InRange(slots,1,2);
            return Task.FromResult(Next!=null&&Claimed<3?new List<PartsAndReplace>{Next()}:new());
        }
        public override Task ExecuteAsync(PartsAndReplace part,string operation,object? data=null,string? error=null,CancellationToken ct=default)
        {
            Assert.Equal("owner-"+part.Id,part.PartsWorkerId);Calls.Enqueue((part.Id,operation));
            if(Failure=="lease")throw new HttpRequestException("stale",null,HttpStatusCode.Conflict);
            if(operation=="name"&&Failure=="save")throw new HttpRequestException("failed save");
            if(operation=="name"&&Failure=="cancel")throw new OperationCanceledException();
            if(operation=="complete"){Results[part.Id]=(PartsAndReplace)data!;if(part.Id==3)ThirdDone.TrySetResult();}
            return Task.CompletedTask;
        }
    }
    private sealed class Parser(Func<string,Task<string>> parse,Action<List<CheckProxyResult>>? inspect=null):IParseService
    {
        public async Task<List<ParsingPart>> Parse(string part,List<CheckProxyResult> sources,CancellationToken ct=default)
        {inspect?.Invoke(sources);var value=await parse(part);return new(){new(){PartSource=sources[0].PartSource,Name="Name"+value}};}
    }
    private sealed class SourcesProvider():SourceProxyProvider(null!,Microsoft.Extensions.Options.Options.Create(new ApiServiceOptions()),
        Microsoft.Extensions.Options.Options.Create(new ModuleOptions()),null!,null!,NullLogger.Instance)
    {
        public int Calls;
        public override Task<List<CheckProxyResult>> GetActiveSourceProxiesAsync(CancellationToken ct=default)
        {Calls++;return Task.FromResult(new List<CheckProxyResult>{new(){PartSource=new PartSource{Id=1,SourceName="test.site",Link="https://test.site/"}}});}
    }
    private sealed class Notifications:ITelegramNotificationService
    {public Task SendTimeoutNotificationAsync(string part,DateTime start)=>Task.CompletedTask;}
    private sealed class NoNetwork:HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new InvalidOperationException("Unexpected external HTTP request.");}
}
