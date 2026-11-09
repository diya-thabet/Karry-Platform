using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>A row of <c>audit.ledger_entries</c> (append-only: triggers reject UPDATE/DELETE/TRUNCATE).</summary>
public sealed class LedgerEntryRow : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public long Seq { get; init; }

    public Guid EventId { get; init; }

    public required string EventType { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>RFC 8785 canonical JSON, stored as text so the exact hashed bytes are kept.</summary>
    public required string Payload { get; init; }

    public required string PayloadHash { get; init; }

    public required string PrevHash { get; init; }

    public required string EntryHash { get; init; }

    public DateTimeOffset RecordedAt { get; init; }
}

/// <summary>The chain head of a tenant: last sequence and hash; locked FOR UPDATE to append gap-free.</summary>
public sealed class LedgerHeadRow : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public long LastSeq { get; init; }

    public required string LastHash { get; init; }
}

/// <summary>
/// What a ledger entry is about (ADR-0029 §1): who acted and on which entity, read from the event's
/// <c>[AuditSubject]</c>. An index for the audit viewer's filters, derived from the hashed payload, never hashed.
/// </summary>
public sealed class LedgerSubjectRow : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public long Seq { get; init; }

    public string? Actor { get; init; }

    public required string EntityType { get; init; }

    public string? EntityId { get; init; }

    /// <summary>The site the entry concerns, when the event names one: site-scoped readers see only their sites' entries.</summary>
    public Guid? SiteId { get; init; }
}

/// <summary>
/// One integrity check of the tenant's ledger and its result (US-P1-AUD-03 AC2). Append-only like the ledger: a past
/// result is evidence of what was found at the time.
/// </summary>
public sealed class VerificationRow : ITenantOwned
{
    public Guid Id { get; init; }

    public TenantId TenantId { get; init; }

    public DateTimeOffset VerifiedAt { get; init; }

    public string? VerifiedBy { get; init; }

    public bool Valid { get; init; }

    public long EntriesChecked { get; init; }

    public long? FirstBrokenSequence { get; init; }

    public string? Problem { get; init; }

    public string? HeadHash { get; init; }

    public int? AnchorsChecked { get; init; }
}

/// <summary>
/// A record's signature envelope as received (ADR-0033 §4), append-only: <see cref="Canonical"/> is the exact text the
/// device signed, so it can be verified again at any time. Written by <see cref="PostgresEnvelopeRecorder"/> with SQL;
/// mapped here for the migration and for reads.
/// </summary>
public sealed class SignatureEnvelopeRow : ITenantOwned
{
    public required Guid EnvelopeId { get; init; }

    public required TenantId TenantId { get; init; }

    public required string RecordType { get; init; }

    public required Guid RecordId { get; init; }

    public required int RecordVersion { get; init; }

    public required string EnvelopeHash { get; init; }

    public required string Canonical { get; init; }

    public required Guid DeviceId { get; init; }

    public required string DeviceSignature { get; init; }

    public required Guid CommandId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset RecordedAt { get; init; }
}

/// <summary>The server's countersignature of an envelope (ADR-0033 §4): written once, never changed (append-only).</summary>
public sealed class EnvelopeCountersignatureRow : ITenantOwned
{
    public required Guid EnvelopeId { get; init; }

    public TenantId TenantId { get; init; }

    /// <summary>The server key version (<c>kid</c>), published in <see cref="ServerKeyRow"/>.</summary>
    public required string KeyId { get; init; }

    /// <summary>ES256, IEEE P1363, base64url, over SHA-256 of <c>Countersignatures.Text</c>.</summary>
    public required string Signature { get; init; }

    public required DateTimeOffset SignedAt { get; init; }
}

/// <summary>A published server key version (ADR-0033 §3): public by design, platform-wide, append-only.</summary>
public sealed class ServerKeyRow
{
    public required string KeyId { get; init; }

    /// <summary>SPKI PEM.</summary>
    public required string PublicKey { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset RecordedAt { get; init; }
}

/// <summary>An anchor written to WORM storage (bookkeeping; the WORM object is the evidence).</summary>
public sealed class AnchorRow : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public long Seq { get; init; }

    public required string EntryHash { get; init; }

    public DateTimeOffset AnchoredAt { get; init; }

    public required string ObjectKey { get; init; }

    public required string KeyId { get; init; }
}

/// <summary>Schema <c>audit</c>: the per-tenant hash-chained ledger (ADR-0008).</summary>
public sealed class TrustDbContext(DbContextOptions<TrustDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "audit";

    public override string Schema => SchemaName;

    public DbSet<LedgerEntryRow> LedgerEntries => Set<LedgerEntryRow>();

    public DbSet<LedgerHeadRow> LedgerHeads => Set<LedgerHeadRow>();

    public DbSet<AnchorRow> Anchors => Set<AnchorRow>();

    public DbSet<LedgerSubjectRow> LedgerSubjects => Set<LedgerSubjectRow>();

    public DbSet<VerificationRow> Verifications => Set<VerificationRow>();

    public DbSet<AuditExport> Exports => Set<AuditExport>();

    public DbSet<SignatureEnvelopeRow> SignatureEnvelopes => Set<SignatureEnvelopeRow>();

    public DbSet<EnvelopeCountersignatureRow> Countersignatures => Set<EnvelopeCountersignatureRow>();

    public DbSet<ServerKeyRow> ServerKeys => Set<ServerKeyRow>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<LedgerEntryRow>(entry =>
        {
            entry.ToTable("ledger_entries");
            entry.HasKey(e => new { e.TenantId, e.Seq });
            entry.HasIndex(e => e.EventId).IsUnique();
            entry.Property(e => e.EventType).HasMaxLength(200);
            entry.Property(e => e.PayloadHash).HasMaxLength(64).IsFixedLength();
            entry.Property(e => e.PrevHash).HasMaxLength(64).IsFixedLength();
            entry.Property(e => e.EntryHash).HasMaxLength(64).IsFixedLength();
            entry.Property(e => e.RecordedAt).HasDefaultValueSql("now()");
        });
        modelBuilder.Entity<LedgerHeadRow>(head =>
        {
            head.ToTable("ledger_heads");
            head.HasKey(h => h.TenantId);
            head.Property(h => h.LastHash).HasMaxLength(64).IsFixedLength();
        });
        modelBuilder.Entity<LedgerSubjectRow>(subject =>
        {
            subject.ToTable("ledger_subjects");
            subject.HasKey(s => new { s.TenantId, s.Seq });
            subject.Property(s => s.Actor).HasMaxLength(200);
            subject.Property(s => s.EntityType).HasMaxLength(50);
            subject.Property(s => s.EntityId).HasMaxLength(200);
            // One index per filter of the audit viewer, newest first within a tenant.
            subject.HasIndex(s => new { s.TenantId, s.Actor, s.Seq });
            subject.HasIndex(s => new { s.TenantId, s.EntityType, s.EntityId, s.Seq });
            subject.HasIndex(s => new { s.TenantId, s.SiteId, s.Seq });
        });
        modelBuilder.Entity<VerificationRow>(verification =>
        {
            verification.ToTable("verifications");
            verification.HasKey(v => v.Id);
            verification.Property(v => v.VerifiedBy).HasMaxLength(200);
            verification.Property(v => v.Problem).HasMaxLength(1000);
            verification.Property(v => v.HeadHash).HasMaxLength(64).IsFixedLength();
            verification.HasIndex(v => new { v.TenantId, v.VerifiedAt });
        });
        modelBuilder.Entity<AuditExport>(export =>
        {
            export.ToTable("exports");
            export.HasKey(e => e.Id);
            export.Property(e => e.Id).HasConversion(id => id.Value, value => new AuditExportId(value)).ValueGeneratedNever();
            export.Property(e => e.ExportedBy).HasMaxLength(200);
            export.Property(e => e.Filters).HasMaxLength(2000);
            export.HasIndex(e => new { e.TenantId, e.ExportedAt });
            export.Ignore(e => e.DomainEvents);
            export.Ignore(e => e.AggregateKey);
        });
        modelBuilder.Entity<SignatureEnvelopeRow>(envelope =>
        {
            envelope.ToTable("signature_envelopes");
            envelope.HasKey(e => e.EnvelopeId);
            envelope.Property(e => e.RecordType).HasMaxLength(100);
            envelope.Property(e => e.EnvelopeHash).HasMaxLength(71);
            envelope.Property(e => e.Canonical).HasMaxLength(8000);
            envelope.Property(e => e.DeviceSignature).HasMaxLength(86);
            envelope.Property(e => e.RecordedAt).HasDefaultValueSql("now()");
            envelope.HasIndex(e => new { e.TenantId, e.RecordType, e.RecordId });
            envelope.HasIndex(e => new { e.TenantId, e.ReceivedAt });
        });
        modelBuilder.Entity<ServerKeyRow>(key =>
        {
            key.ToTable("server_keys");
            key.HasKey(k => k.KeyId);
            key.Property(k => k.KeyId).HasMaxLength(100);
            key.Property(k => k.PublicKey).HasMaxLength(1000);
            key.Property(k => k.RecordedAt).HasDefaultValueSql("now()");
        });
        modelBuilder.Entity<EnvelopeCountersignatureRow>(countersignature =>
        {
            countersignature.ToTable("envelope_countersignatures");
            countersignature.HasKey(c => c.EnvelopeId);
            countersignature.Property(c => c.KeyId).HasMaxLength(100);
            countersignature.Property(c => c.Signature).HasMaxLength(86);
            countersignature.HasOne<SignatureEnvelopeRow>().WithOne().HasForeignKey<EnvelopeCountersignatureRow>(c => c.EnvelopeId).OnDelete(DeleteBehavior.Restrict);
            countersignature.HasOne<ServerKeyRow>().WithMany().HasForeignKey(c => c.KeyId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AnchorRow>(anchor =>
        {
            anchor.ToTable("anchors");
            anchor.HasKey(a => new { a.TenantId, a.Seq });
            anchor.Property(a => a.EntryHash).HasMaxLength(64).IsFixedLength();
            anchor.Property(a => a.ObjectKey).HasMaxLength(100);
            anchor.Property(a => a.KeyId).HasMaxLength(64);
        });
    }
}
