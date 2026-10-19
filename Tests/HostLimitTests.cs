using KameraData.Data.Models;
using Microsoft.Extensions.Options;
using PAR.ParseLib;
using Xunit;

namespace PAR.PartsGrabber.Tests;

public sealed class HostLimitTests
{
    [Fact]
    public async Task SameHostIsSerializedAcrossParts_WhileDifferentHostsCanProceed()
    {
        var limits=new SharedProcessingLimits(Microsoft.Extensions.Options.Options.Create(new ModuleOptions()));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered=false;
        var parser=new FakeParser(async part=>{
            if(part=="first"){entered.SetResult();await release.Task;}
            if(part=="second")secondEntered=true;
        });
        var firstService=new HostLimitedParseService(new ParseService(new Factory(parser)),limits);
        var secondService=new HostLimitedParseService(new ParseService(new Factory(parser)),limits);
        List<CheckProxyResult> Source(string host)=>new(){new(){PartSource=new PartSource{SourceName=host,Link="https://"+host+"/"}}};
        var first=firstService.Parse("first",Source("www.example.test"));await entered.Task;
        var second=secondService.Parse("second",Source("example.test"));
        await secondService.Parse("third",Source("other.test"));
        Assert.False(secondEntered);Assert.False(second.IsCompleted);
        release.SetResult();await Task.WhenAll(first,second);Assert.True(secondEntered);
    }
    private sealed class Factory(IParser parser):IParsersFactory{public IParser GetParser(string site)=>parser;}
    private sealed class FakeParser(Func<string,Task> parse):IParser
    {
        public string SiteUrl=>"example.test";
        public async Task<ParsingPart> Parse(PartSource source,string part,List<Proxy> proxies,CancellationToken ct=default)
        {await parse(part);return new(){PartSource=source};}
    }
}
