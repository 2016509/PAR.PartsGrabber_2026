using KameraData.Data.Models;
using PAR.ParseLib;

namespace PAR.PartsGrabber;

public sealed class LeaseAwareApiService(ApiService inner):IApiService
{
    public Task<List<T>> Get<T>(string url,CancellationToken ct=default) where T:class,new()=>inner.Get<T>(url,ct);
    public Task<T> GetSingle<T>(string url,CancellationToken ct=default) where T:class,new()=>inner.GetSingle<T>(url,ct);
    public Task<List<R>> PostForList<T,R>(string url,T model,CancellationToken ct=default) where T:class,new() where R:class,new()=>inner.PostForList<T,R>(url,model,ct);
    public Task Post<T>(string url,T model,CancellationToken ct=default) where T:class,new()
    {
        ct.ThrowIfCancellationRequested();
        var operation=model switch{PartsNamesArchive=>"name",ReplacesArchive=>"replace",PartsPicArchive=>"picture",_=>null};
        return PartProcessingLease.Current is { } lease&&operation!=null?lease.WriteAsync(operation,model,ct):inner.Post(url,model,ct);
    }
    public Task Put<T>(string url,T model,CancellationToken ct=default) where T:class,new()
    {
        ct.ThrowIfCancellationRequested();
        return PartProcessingLease.Current is { } lease&&model is PartsAndReplace?lease.WriteAsync("complete",model,ct):inner.Put(url,model,ct);
    }
}
