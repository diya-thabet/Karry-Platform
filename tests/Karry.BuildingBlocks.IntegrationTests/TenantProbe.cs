using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.BuildingBlocks.IntegrationTests;

// A minimal tenant-owned module used only by the tenant-isolation tests: an aggregate, its event, and a tenant-owned
// projection written by a subscriber in the dispatch transaction.

public readonly record struct SiteNoteId(Guid Value) : IEntityId<SiteNoteId>
{
    public static SiteNoteId From(Guid value) => new(value);
}

[EventType("tenantprobe.note_written.v1")]
[AuditSubject("note", nameof(NoteId))]
public sealed record NoteWritten(Guid NoteId, string Text) : DomainEvent;

public sealed class SiteNote : AggregateRoot<SiteNoteId>, ITenantOwned
{
    private SiteNote(SiteNoteId id, TenantId tenantId)
        : base(id)
    {
        TenantId = tenantId;
    }

    public TenantId TenantId { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public static SiteNote Write(TimeProvider clock, TenantId tenant, string text)
    {
        var note = new SiteNote(EntityId.New<SiteNoteId>(clock), tenant) { Text = text };
        note.Raise(new NoteWritten(note.Id.Value, text) { OccurredAt = clock.GetUtcNow() });
        return note;
    }
}

/// <summary>Projection row: written by <see cref="NoteCopier"/> for the tenant of the event.</summary>
public sealed class NoteCopy : ITenantOwned
{
    public Guid Id { get; init; }

    public TenantId TenantId { get; init; }

    public required string Text { get; init; }
}

public sealed class TenantProbeDbContext(DbContextOptions<TenantProbeDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "tenantprobe";

    public override string Schema => SchemaName;

    public DbSet<SiteNote> Notes => Set<SiteNote>();

    public DbSet<NoteCopy> Copies => Set<NoteCopy>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SiteNote>(note =>
        {
            note.ToTable("site_notes");
            note.HasKey(n => n.Id);
            note.Property(n => n.Id).HasConversion(id => id.Value, value => SiteNoteId.From(value));
            note.Ignore(n => n.DomainEvents);
            note.Ignore(n => n.AggregateKey);
        });
        modelBuilder.Entity<NoteCopy>(copy =>
        {
            copy.ToTable("note_copies");
            copy.HasKey(c => c.Id);
        });
    }
}

/// <summary>
/// Writes the tenant-owned copy in the dispatch transaction (context.Db). Succeeds under RLS only because the
/// dispatcher put the event's tenant into the scope; the copy is stamped with that scope's tenant.
/// </summary>
public sealed class NoteCopier(Tenancy.ITenantContext tenant, TimeProvider clock) : IDomainEventSubscriber<NoteWritten>
{
    public Task HandleAsync(NoteWritten domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);
        context.Db.Set<NoteCopy>().Add(new NoteCopy
        {
            Id = Guid.CreateVersion7(clock.GetUtcNow()),
            TenantId = tenant.Current ?? throw new InvalidOperationException("No tenant in the dispatch scope."),
            Text = domainEvent.Text,
        });
        return Task.CompletedTask;
    }
}
