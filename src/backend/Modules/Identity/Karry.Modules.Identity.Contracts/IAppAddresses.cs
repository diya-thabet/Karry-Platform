namespace Karry.Modules.Identity.Contracts;

/// <summary>
/// Where a company's apps are (<c>Identity:PortalOrigin</c>): links in messages sent to its people (an e-mail digest,
/// ADR-0039 §4) point there.
/// </summary>
public interface IAppAddresses
{
    /// <summary>The company's portal, from its slug (<c>https://{slug}.karry.app</c> in production).</summary>
    Uri PortalFor(string slug);
}
