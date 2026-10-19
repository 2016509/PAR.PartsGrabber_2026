using System.Net;
using System.Text;
using KameraData.Data.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using PAR.ParseLib;
using Xunit;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace PAR.PartsGrabber.Tests;

public sealed class ServiceRoutingTests
{
    private static ApiServiceOptions Config() => new()
    {
        BaseUrl = "http://legacy.test/api",
        ServiceUrls = new() { Parts="http://parts.test/api/", Proxy="http://proxy.test/api", PartSource="http://sources.test/api", ErrorLog="http://errors.test/api" }
    };

    [Fact]
    public void DedicatedServicesAreUsedAndIncompleteConfigurationIsRejected()
    {
        var options=Config(); options.ValidateEndpoints();
        Assert.Equal("http://parts.test/api",options.PartsRoot);
        Assert.Equal("http://proxy.test/api",options.ProxyRoot);
        Assert.Equal("http://sources.test/api",options.SourcesRoot);
        Assert.Equal("http://errors.test/api",options.ErrorsRoot);
        options.ServiceUrls.Proxy=null;
        Assert.Throws<InvalidOperationException>(()=>options.ValidateEndpoints());
        options.ServiceUrls=new();
        Assert.Equal("http://legacy.test/api",options.PartsRoot);
    }

    [Fact]
    public async Task ClaimRenewAndArchiveWritesGoToPartsMicroservice()
    {
        var handler=new Recorder();
        using var http=new HttpClient(handler);
        var queue=new PartsQueueClient(http,OptionsFactory.Create(Config()),OptionsFactory.Create(new ModuleOptions()));
        await queue.ClaimAsync(1);
        var part=new PartsAndReplace{Id=42,PartsWorkerId="owner",PartsAttemptCount=1};
        await queue.ExecuteAsync(part,"renew");
        await queue.ExecuteAsync(part,"name",new PartsNamesArchive{PartsAndReplacesId=42,PartName="test"});
        Assert.Equal(new[]{"/api/PartsAndReplaces/continuous/claim","/api/PartsAndReplaces/continuous/42/renew","/api/PartsAndReplaces/continuous/42/name"},handler.Paths);
    }

    private sealed class Recorder:HttpMessageHandler
    {
        public List<string> Paths {get;}=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("parts.test",request.RequestUri!.Host);
            Paths.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]",Encoding.UTF8,"application/json")});
        }
    }

    [Fact]
    public async Task SourceProxyAndCacheReadsUseTheirOwnServices()
    {
        var options=Config(); options.GetProxiesUrl="/Proxy/get"; options.GetPartsSourcesUrl="/PartSource/getAll";
        options.GetCachedPartData="/PartsGrabberSites/get";
        var api=new ReadApi(); var metrics=new ModuleMetrics(NullLogger<ModuleMetrics>.Instance);
        var provider=new SourceProxyProvider(api,OptionsFactory.Create(options),
            OptionsFactory.Create(new ModuleOptions{UseExternalSourceHealthChecker=true}),null!,metrics,NullLogger.Instance);
        var sources=await provider.GetActiveSourceProxiesAsync();
        var cache=new DatabasePartsService(api,OptionsFactory.Create(options),NullLogger.Instance,metrics);
        var part=await cache.GetPartDataFromDatabaseAsync("W123",sources.Single().PartSource);
        Assert.NotNull(part);
        Assert.Equal(new[]{"proxy.test","sources.test","parts.test"},api.Addresses.Select(x=>x.Host));
    }

    private sealed class ReadApi:IApiService
    {
        public List<Uri> Addresses {get;}=new();
        public Task<List<T>> Get<T>(string url,CancellationToken ct=default) where T:class,new()
        {
            Addresses.Add(new Uri(url));
            object result=typeof(T)==typeof(Proxy)?new List<Proxy>{new(){Id=1}}:
                new List<PartSource>{new(){Id=1,SourceName="example.test",Status=false}};
            return Task.FromResult((List<T>)result);
        }
        public Task<T> GetSingle<T>(string url,CancellationToken ct=default) where T:class,new()
        {
            Addresses.Add(new Uri(url));
            return Task.FromResult((T)(object)new PAR.PartsGrabber.Options.CachedPartData{SitePartNumber="W123",Name="test"});
        }
        public Task<List<R>> PostForList<T,R>(string url,T model,CancellationToken ct=default) where T:class,new() where R:class,new()=>throw new NotSupportedException();
        public Task Post<T>(string url,T model,CancellationToken ct=default) where T:class,new()=>throw new NotSupportedException();
        public Task Put<T>(string url,T model,CancellationToken ct=default) where T:class,new()=>throw new NotSupportedException();
    }
}
