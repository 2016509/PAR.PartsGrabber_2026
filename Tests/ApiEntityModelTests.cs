using KameraData.Data;
using KameraData.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class ApiEntityModelTests
{
    [Fact]
    public void SharedContextBuildsValidModelForSourceAndProxyReads()
    {
        using var context = new KameraDbContext(new DbContextOptionsBuilder<KameraDbContext>()
            .UseSqlServer("Server=localhost;Database=model_validation;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.NotNull(context.Model.FindEntityType(typeof(PartSource)));
        Assert.NotNull(context.Model.FindEntityType(typeof(Proxy)));
        Assert.Equal("parts_worker_id", context.Model.FindEntityType(typeof(PartsAndReplace))!
            .FindProperty(nameof(PartsAndReplace.PartsWorkerId))!.GetColumnName());
    }
}
