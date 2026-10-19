using Dapper;
using KameraData.Data.Models;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using SystemCheckerMssqlWebAPI.Services;
using Xunit;

[assembly:CollectionBehavior(DisableTestParallelization=true)]
namespace PAR.PartsGrabber.Tests;

public sealed class PartsSqlFactAttribute:FactAttribute
{
    public PartsSqlFactAttribute(){if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARTS_GRABBER_TEST_CONNECTION")))Skip="Requires explicit test SQL connection.";}
}

public sealed class PartsLeaseSqlTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly SqlConnection Db=new(Environment.GetEnvironmentVariable("PARTS_GRABBER_TEST_CONNECTION"));
        public SqlTransaction Tx=null!;
        public PartsGrabberQueue Queue=>new("",Db,Tx);
        public int Id;
        public async Task Start()
        {
            await Db.OpenAsync();Tx=Db.BeginTransaction();
            Id=await Db.ExecuteScalarAsync<int>("""
DECLARE @ids TABLE(id int);
INSERT dbo.parts_and_replaces(main_part_number,status,user_created,date_created,record_id,date_update,
parts_worker_id,parts_lease_until_utc,parts_attempt_count)
OUTPUT inserted.id INTO @ids
VALUES(@number,'1','QA-CONTINUOUS-PARTS',SYSUTCDATETIME(),0,SYSUTCDATETIME(),'owner',DATEADD(minute,10,SYSUTCDATETIME()),1);
SELECT id FROM @ids;
""",new{number="QA"+Guid.NewGuid().ToString("N")[..16]},Tx);
        }
        public PartsGrabberRequest Token(string owner="owner",int attempt=1,object? data=null)=>new(){WorkerId=owner,AttemptCount=attempt,MaxAttempts=3,RetryDelaySeconds=1,Data=JsonSerializer.SerializeToElement(data),Error="test failure"};
        public Task<int> Sql(string sql)=>Db.ExecuteAsync(sql,new{id=Id},Tx);
        public async Task<T> Value<T>(string expression)=>(await Db.ExecuteScalarAsync<T>(expression,new{id=Id},Tx))!;
        public async ValueTask DisposeAsync(){await Tx.RollbackAsync();await Tx.DisposeAsync();await Db.DisposeAsync();}
    }

    [PartsSqlFact]
    public async Task LeaseChecksOwnerAttemptAndExpiryBeforeAnyWrite()
    {
        await using var f=new Fixture();await f.Start();
        foreach(var operation in new[]{"renew","fail","name","complete"}) {
            Assert.False(await f.Queue.ExecuteAsync(f.Id,operation,f.Token("wrong")));
            Assert.False(await f.Queue.ExecuteAsync(f.Id,operation,f.Token(attempt:2)));
        }
        Assert.True(await f.Queue.ExecuteAsync(f.Id,"renew",f.Token()));
        await f.Sql("UPDATE dbo.parts_and_replaces SET parts_lease_until_utc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id");
        Assert.False(await f.Queue.ExecuteAsync(f.Id,"renew",f.Token()));
        Assert.False(await f.Queue.ExecuteAsync(f.Id,"fail",f.Token()));
    }

    [PartsSqlFact]
    public async Task ClaimsAndFailuresHaveBoundedRetries_AndDoNotStealLiveLease()
    {
        await using var f=new Fixture();await f.Start();var request=f.Token();request.BatchSize=2;
        Assert.Empty(await f.Queue.ClaimAsync(request,f.Id));
        for(var attempt=1;attempt<=3;attempt++) {
            Assert.True(await f.Queue.ExecuteAsync(f.Id,"fail",f.Token(attempt:attempt)));
            Assert.Empty(await f.Queue.ClaimAsync(request,f.Id));
            await f.Sql("UPDATE dbo.parts_and_replaces SET parts_lease_until_utc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id");
            var rows=(await f.Queue.ClaimAsync(request,f.Id)).ToArray();
            if(attempt<3){Assert.Single(rows);Assert.Equal(attempt+1,rows[0].PartsAttemptCount);}
            else Assert.Empty(rows);
        }
        Assert.Equal("3",await f.Value<string>("SELECT status FROM dbo.parts_and_replaces WHERE id=@id"));
    }

    [PartsSqlFact]
    public async Task ArchiveWritesAreIdempotent_AndCrossPartWritesRejected()
    {
        await using var f=new Fixture();await f.Start();
        var source=await f.Value<int>("SELECT TOP(1) id FROM dbo.parts_sources ORDER BY id");
        var name=new PartsNamesArchive{PartsAndReplacesId=f.Id,PartsSourcesId=source,PartName="QA CONTINUOUS NAME"};
        Assert.True(await f.Queue.ExecuteAsync(f.Id,"name",f.Token(data:name)));
        Assert.True(await f.Queue.ExecuteAsync(f.Id,"name",f.Token(data:name)));
        Assert.Equal(1,await f.Value<int>("SELECT COUNT(*) FROM dbo.parts_names_archive WHERE parts_and_replaces_id=@id"));
        name.PartsAndReplacesId++;
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Queue.ExecuteAsync(f.Id,"name",f.Token(data:name)));
        Assert.Equal(1,await f.Value<int>("SELECT COUNT(*) FROM dbo.parts_names_archive WHERE parts_and_replaces_id=@id"));
    }

    [PartsSqlFact]
    public async Task CompletionRetainsReceiptAndCannotBeOverwrittenByOldAttempt()
    {
        await using var f=new Fixture();await f.Start();var result=new PartsAndReplace{Id=f.Id,Status="2",PartName="QA FINAL",Replaces="[]"};
        Assert.True(await f.Queue.ExecuteAsync(f.Id,"complete",f.Token(data:result)));
        Assert.True(await f.Queue.ExecuteAsync(f.Id,"complete",f.Token(data:result)));
        Assert.Equal("QA FINAL",await f.Value<string>("SELECT part_name FROM dbo.parts_and_replaces WHERE id=@id"));
        Assert.False(await f.Queue.ExecuteAsync(f.Id,"name",f.Token()));
        await f.Sql("UPDATE dbo.parts_and_replaces SET status='1',parts_attempt_count=2,parts_worker_id='next-owner',parts_lease_until_utc=DATEADD(minute,10,SYSUTCDATETIME()) WHERE id=@id");
        Assert.False(await f.Queue.ExecuteAsync(f.Id,"complete",f.Token(data:result)));
    }
}
