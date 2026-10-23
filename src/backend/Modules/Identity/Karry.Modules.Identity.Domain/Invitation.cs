using System.Security.Cryptography;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Domain;

public readonly record struct InvitationId(Guid Value) : IEntityId<InvitationId>
{
    public static InvitationId From(Guid value) => new(value);
}

public enum InvitationChannel
{
    Email = 1,

    /// <summary>SMS or WhatsApp link, through the messaging port (provider chosen by the owner, stubbed until then).</summary>
    Phone = 2,
}

/// <summary>
/// A single-use link to activate an office account (US-P1-IAM-03 AC1, TC-P1-IAM-008): valid 7 days, then refused;
/// refused once used. Only the SHA-256 of the token is stored. The token starts with the tenant ID so the anonymous
/// accept request can be served under that tenant's row-level security without any cross-tenant lookup.
/// </summary>
public sealed class Invitation : AggregateRoot<InvitationId>, ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private Invitation(InvitationId id, TenantId tenantId, UserId userId, InvitationChannel channel, string tokenHash, DateTimeOffset createdAt)
        : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Channel = channel;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + Lifetime;
    }

    public TenantId TenantId { get; private set; }

    public UserId UserId { get; private set; }

    public InvitationChannel Channel { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the token (what is stored and looked up).</summary>
    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>The tenant named by a token (<c>&lt;tenant uuid N&gt;.&lt;256-bit secret&gt;</c>), or null when it is malformed.</summary>
    public static TenantId? TenantOf(string? token) =>
        token is { Length: > 33 } && token[32] == '.' && Guid.TryParseExact(token[..32], "N", out var tenant) && tenant != Guid.Empty
            ? new TenantId(tenant)
            : null;

    internal static (Invitation Invitation, string Token) Issue(TimeProvider clock, TenantId tenant, UserId user, InvitationChannel channel)
    {
        var token = $"{tenant.Value:N}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        return (new Invitation(EntityId.New<InvitationId>(clock), tenant, user, channel, HashToken(token), clock.GetUtcNow()), token);
    }

    internal Result<Invitation> Accept(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        if (AcceptedAt is not null)
        {
            return Error.Conflict("identity.invitation_used", "This invitation was already used.");
        }

        if (now >= ExpiresAt)
        {
            return Error.BusinessRule("identity.invitation_expired", "This invitation has expired. Ask your administrator for a new one.");
        }

        AcceptedAt = now;
        return this;
    }
}
