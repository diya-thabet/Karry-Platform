using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class TrustDbContextFactory : IDesignTimeDbContextFactory<TrustDbContext>
{
    public TrustDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TrustDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
