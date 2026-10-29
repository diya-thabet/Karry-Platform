using Karry.SharedKernel.Tenancy;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>
/// A Karry operator's call inside a support session (ADR-0029 §4): read only, on the session's tenant, with the
/// built-in <c>support</c> access. Set by <see cref="TenantResolutionMiddleware"/>; null for every other caller.
/// </summary>
public sealed record SupportCaller(TenantId Tenant, Guid SessionId, string OperatorSubject);

/// <summary>Scoped holder of <see cref="SupportCaller"/> for the current request.</summary>
public sealed class SupportCallContext
{
    public SupportCaller? Current { get; private set; }

    public void Set(SupportCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (Current is not null)
        {
            throw new InvalidOperationException("The support caller is already set for this request.");
        }

        Current = caller;
    }
}

/// <summary>
/// The support sessions of the current tenant, as the middleware needs them (implemented by the Identity module).
/// Called with the session's tenant set, so a session of another tenant is simply not found (row-level security).
/// </summary>
public interface ISupportSessionAccess
{
    /// <summary>True when the session exists, has not ended, is within its hour and belongs to this operator.</summary>
    Task<bool> IsActiveAsync(Guid sessionId, string operatorSubject, CancellationToken cancellationToken);

    /// <summary>Records one request of the session in the tenant's ledger (<c>identity.support_request.v1</c>).</summary>
    Task RecordRequestAsync(TenantId tenant, Guid sessionId, string method, string path, int status, CancellationToken cancellationToken);
}

/// <summary>
/// The <c>Karry-Support-Session</c> header: <c>&lt;tenantId&gt;.&lt;sessionId&gt;</c> (two UUIDs). It names the tenant, the
/// way a tablet's assertion does, so the session is looked up under that tenant's row-level security.
/// </summary>
public static class SupportSessionHeader
{
    public const string Name = "Karry-Support-Session";

    public static string Value(TenantId tenant, Guid sessionId) => $"{tenant.Value:D}.{sessionId:D}";

    /// <summary>The tenant and session named by the header, or null when it is not exactly two non-empty UUIDs.</summary>
    public static (TenantId Tenant, Guid SessionId)? Parse(string? value)
    {
        var parts = value?.Split('.');
        return parts is [var tenant, var session]
            && Guid.TryParseExact(tenant, "D", out var tenantId) && tenantId != Guid.Empty
            && Guid.TryParseExact(session, "D", out var sessionId) && sessionId != Guid.Empty
            ? (new TenantId(tenantId), sessionId)
            : null;
    }
}
