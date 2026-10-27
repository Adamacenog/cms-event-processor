using CmsEventProcessor.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace CmsEventProcessor.UnitTests.Processing;

/// <summary>
/// Verifies the application-level read boundary without requiring a reachable database.
/// </summary>
public sealed class ReadContextTests
{
    /// <summary>
    /// Every synchronous/asynchronous SaveChanges overload rejects writes before any SQL connection opens.
    /// </summary>
    /// <returns>A task completing after all four save overloads reject the operation.</returns>
    [Fact]
    public async Task EverySaveOverloadRejectsWrites()
    {
        var options = new DbContextOptionsBuilder<ReadContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=Unused;Integrated Security=true").Options;
        await using var context = new ReadContext(options);
        Assert.Equal(QueryTrackingBehavior.NoTracking, context.ChangeTracker.QueryTrackingBehavior);
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(false));
    }
}
