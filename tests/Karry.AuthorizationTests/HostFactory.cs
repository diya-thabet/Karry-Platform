using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Karry.AuthorizationTests;

/// <summary>The real host without a database: every module maps its endpoints and declares its authorization.</summary>
internal sealed class HostFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Karry", string.Empty);
    }
}
