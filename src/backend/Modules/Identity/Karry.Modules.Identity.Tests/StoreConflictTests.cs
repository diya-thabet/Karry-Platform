using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Karry.Modules.Identity.Tests;

/// <summary>
/// Two people changing the same record at once (hardening plan H-C1), deterministically: when the save finds the row
/// changed under it, the store answers a conflict (try again) and forgets what it tracked. The HTTP tests reach this
/// only when a race happens to be lost; these always do, without a database.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class StoreConflictTests
{
    [Fact]
    public async Task The_identity_store_answers_a_version_conflict_when_someone_else_saved_first()
    {
        await using var db = LosingContext();

        var result = await new IdentityStore(db).SaveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(RecordVersions.Conflict);
    }

    [Fact]
    public async Task The_device_store_answers_a_conflict_when_someone_else_saved_first()
    {
        await using var db = LosingContext();

        var result = await new DeviceStore(db, new TenantContext()).SaveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error?.Code.Should().Be("identity.concurrent_change");
    }

    /// <summary>A context whose every save finds the row changed by someone else; it never opens a connection.</summary>
    private static IdentityDbContext LosingContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=karry")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new LosesTheRace())
            .Options);

    private sealed class LosesTheRace : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("The row was changed by someone else.");
    }
}
