namespace Karry.BuildingBlocks.Tenancy;

/// <summary>Configuration section <c>Tenancy</c>.</summary>
public sealed class TenancyOptions
{
    public const string Section = "Tenancy";

    /// <summary>
    /// Domain under which each tenant has its own address (<c>karry.app</c> → <c>acme.karry.app</c>). When a request
    /// arrives on such an address, the token's tenant must be the tenant of that slug. Empty: no address check.
    /// </summary>
    public string? BaseDomain { get; set; }
}
