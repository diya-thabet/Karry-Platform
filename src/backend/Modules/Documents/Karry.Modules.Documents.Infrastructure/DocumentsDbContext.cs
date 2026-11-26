using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Documents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Documents.Infrastructure;

/// <summary>Schema <c>documents</c> (tenant-owned, RLS): one envelope per issued document (ADR-0040 §3).</summary>
public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "documents";

    public override string Schema => SchemaName;

    public DbSet<DocumentEnvelope> Envelopes => Set<DocumentEnvelope>();

    /// <summary>Global (no RLS, declared): read by the public verification before any company is known.</summary>
    public DbSet<VerificationKey> VerificationKeys => Set<VerificationKey>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<DocumentEnvelope>(envelope =>
        {
            envelope.ToTable("envelopes");
            envelope.HasKey(e => e.Id);
            envelope.Ignore(e => e.DomainEvents);
            envelope.Property(e => e.Id).HasConversion(id => id.Value, value => new DocumentEnvelopeId(value)).ValueGeneratedNever();
            envelope.Property(e => e.Type).HasMaxLength(40);
            envelope.Property(e => e.ShiftCode).HasMaxLength(20);
            envelope.Property(e => e.ContentHash).HasMaxLength(71);
            envelope.Property(e => e.PdfHash).HasMaxLength(71);
            envelope.Property(e => e.TokenHash).HasMaxLength(64);
            envelope.Property(e => e.IssuedBy).HasMaxLength(200);
            envelope.Property(e => e.Signature).HasMaxLength(200);
            envelope.Property(e => e.SignatureKeyId).HasMaxLength(100);
            envelope.HasIndex(e => new { e.TenantId, e.SiteId, e.ShiftDate, e.ShiftCode });
            // The Workers sign the envelopes still without a signature (slice 3c).
            envelope.HasIndex(e => new { e.TenantId, e.SignedAt });
        });
        modelBuilder.Entity<VerificationKey>(key =>
        {
            key.ToTable("verification_keys");
            key.HasKey(k => k.EnvelopeId);
            key.Property(k => k.EnvelopeId).ValueGeneratedNever();
            key.Property(k => k.TokenHash).HasMaxLength(64);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class DocumentsDbContextFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DocumentsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
