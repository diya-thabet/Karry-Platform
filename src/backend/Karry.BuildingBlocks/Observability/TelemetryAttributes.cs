using System.Collections.Frozen;

namespace Karry.BuildingBlocks.Observability;

/// <summary>
/// Span attributes Karry exports (US-P1-OPS-01 AC1). Telemetry leaves the trust boundary (collector, vendor), so it is
/// <b>allow-listed</b>: <see cref="AttributeAllowListProcessor"/> removes every attribute not listed here. Adding an
/// attribute is a reviewed change: it must never carry personal data (names, e-mail, phone, tokens, free text).
/// </summary>
public static class TelemetryAttributes
{
    /// <summary>Tenant of the request (a UUID, not personal data); <see cref="NoTenant"/> until tenancy resolves it (P1.5).</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Module that served the request (<c>IModule.Name</c>), <see cref="HostModule"/> or <see cref="Unmatched"/>.</summary>
    public const string Module = "module";

    /// <summary>Route template (<c>/api/v1/assets/{id}</c>), never the concrete path, which can contain identifiers.</summary>
    public const string Route = "route";

    /// <summary>Role of the caller; <see cref="AnonymousRole"/> until authentication sets it (P1.8/P1.9).</summary>
    public const string UserRole = "user_role";

    /// <summary>Stable event type of an outbox message (<c>assets.meter_reading_recorded.v1</c>).</summary>
    public const string EventType = "event_type";

    public const string NoTenant = "none";
    public const string AnonymousRole = "anonymous";
    public const string HostModule = "host";
    public const string Unmatched = "unmatched";

    /// <summary>
    /// Attributes kept on spans: Karry's own plus the OpenTelemetry semantic conventions that describe the operation
    /// without its data. Deliberately excluded: <c>url.full</c>, <c>url.path</c>, <c>url.query</c> (identifiers and
    /// query strings), <c>user_agent.original</c>, <c>client.address</c>, <c>network.peer.address</c> (personal data),
    /// <c>exception.message</c> (can quote user input).
    /// <c>db.query.text</c> is kept: EF Core and Npgsql send parameterized SQL, values are never part of the text.
    /// </summary>
    public static readonly FrozenSet<string> Allowed = FrozenSet.Create(StringComparer.Ordinal,
    [
        TenantId, Module, Route, UserRole, EventType,
        "http.request.method", "http.response.status_code", "http.route", "url.scheme",
        "network.protocol.name", "network.protocol.version", "server.address", "server.port", "error.type",
        "db.system", "db.system.name", "db.namespace", "db.operation.name", "db.collection.name",
        "db.query.text", "db.response.status_code", "db.npgsql.data_source",
        "otel.status_code", "exception.type",
    ]);
}
