using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PAR.ParseLib;

namespace PAR.PartsGrabber;

public sealed class SharedProcessingLimits(IOptions<ModuleOptions> options)
{
    public SemaphoreSlim Saves {get;}=new(Math.Max(1,options.Value.MaxConcurrentSaves));
    private readonly ConcurrentDictionary<string,SemaphoreSlim> hosts=new(StringComparer.OrdinalIgnoreCase);
    public SemaphoreSlim Host(CheckProxyResult source)
    {
        var key=Uri.TryCreate(source.PartSource.Link,UriKind.Absolute,out var uri)?uri.Host:source.PartSource.SourceName;
        return hosts.GetOrAdd(key.Replace("www.","",StringComparison.OrdinalIgnoreCase),_=>new SemaphoreSlim(1));
    }
}

public sealed class HostLimitedParseService(ParseService inner,SharedProcessingLimits limits):IParseService
{
    public async Task<List<ParsingPart>> Parse(string part,List<CheckProxyResult> sources,CancellationToken ct=default)
    {
        var tasks=sources.Select(async source=>{
            var gate=limits.Host(source);await gate.WaitAsync(ct);
            try{return await inner.Parse(part,new(){source},ct);}finally{gate.Release();}
        });
        return (await Task.WhenAll(tasks)).SelectMany(x=>x).ToList();
    }
}
