using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Billing.Infrastructure;

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BillingDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
