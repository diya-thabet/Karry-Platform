using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Domain;

public readonly record struct AuditExportId(Guid Value) : IEntityId<AuditExportId>
{
    public static AuditExportId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A CSV export of the audit log (US-P1-AUD-04 AC2): who took which entries out of Karry, with which filters. Its
/// event is itself an entry of the company's ledger (ADR-0029 §2), so reading the trail leaves a trail.
/// </summary>
[EventType("trust.audit_exported.v1")]
[AuditSubject("audit_export", nameof(ExportId))]
public sealed record AuditExported(Guid ExportId, string Filters, long Rows, long? UpToSequence, string? Actor) : DomainEvent;

public sealed class AuditExport : AggregateRoot<AuditExportId>, ITenantOwned
{
    private AuditExport(AuditExportId id, TenantId tenantId, string filters)
        : base(id)
    {
        TenantId = tenantId;
        Filters = filters;
    }

    public TenantId TenantId { get; private set; }

    public DateTimeOffset ExportedAt { get; private set; }

    public string? ExportedBy { get; private set; }

    /// <summary>The filters as the caller gave them, as JSON (what was asked for, not only what came out).</summary>
    public string Filters { get; private set; }

    public long Rows { get; private set; }

    /// <summary>The newest sequence the export covers: entries written meanwhile (this export's own among them) are not in it.</summary>
    public long? UpToSequence { get; private set; }

    public static AuditExport Record(TimeProvider clock, TenantId tenant, string filters, long rows, long? upToSequence, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(filters);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        var export = new AuditExport(EntityId.New<AuditExportId>(clock), tenant, filters)
        {
            ExportedAt = clock.GetUtcNow(),
            ExportedBy = actor,
            Rows = rows,
            UpToSequence = upToSequence,
        };
        export.Raise(new AuditExported(export.Id.Value, filters, rows, upToSequence, actor) { OccurredAt = export.ExportedAt });
        return export;
    }
}
