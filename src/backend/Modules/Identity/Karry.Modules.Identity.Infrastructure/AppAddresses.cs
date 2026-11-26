using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Contracts;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>The apps' addresses as Identity's settings give them (the same as the invitation links).</summary>
internal sealed class AppAddresses(IOptions<IdentityOptions> options) : IAppAddresses
{
    public Uri PortalFor(string slug) => options.Value.PortalFor(slug);
}
