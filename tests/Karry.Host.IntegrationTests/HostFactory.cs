using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Karry.Host.IntegrationTests;

/// <summary>Hosts <c>Karry.Host</c> in memory with an explicit connection string (or none).</summary>
internal sealed class HostFactory(string? connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" keeps appsettings.Development.json (local stack connection string) out of the tests.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Karry", connectionString ?? string.Empty);
    }
}
