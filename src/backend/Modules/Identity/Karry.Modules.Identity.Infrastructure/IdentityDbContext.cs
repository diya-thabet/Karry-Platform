using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>Position of an importer (e.g. Keycloak sign-in failures): platform data, one row per source.</summary>
public sealed class ImportCursorRow
{
    public required string Source { get; set; }

    public DateTimeOffset Position { get; set; }
}

/// <summary>Schema <c>identity</c>: users, invitations and sign-in failures (tenant-owned, row-level security) and importer cursors.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "identity";

    private static readonly JsonSerializerOptions RolesJson = new(JsonSerializerDefaults.Web);

    public override string Schema => SchemaName;

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<SignInFailure> SignInFailures => Set<SignInFailure>();

    public DbSet<ImportCursorRow> ImportCursors => Set<ImportCursorRow>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<UserAccount>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).HasConversion(id => id.Value, value => new UserId(value)).ValueGeneratedNever();
            user.Property(u => u.Kind).HasConversion<string>().HasMaxLength(10);
            user.Property(u => u.Status).HasConversion<string>().HasMaxLength(12);
            user.Property(u => u.FirstName).HasMaxLength(UserAccount.NameMaxLength);
            user.Property(u => u.LastName).HasMaxLength(UserAccount.NameMaxLength);
            user.Property(u => u.Email).HasMaxLength(254);
            user.Property(u => u.Phone).HasMaxLength(16);
            user.Property(u => u.EmployeeId).HasMaxLength(UserAccount.EmployeeIdMaxLength);
            user.Property(u => u.Subject).HasMaxLength(64);
            user.Property(u => u.PinHash).HasMaxLength(120);
            user.Property(u => u.Roles).HasColumnType("jsonb").HasConversion(
                roles => JsonSerializer.Serialize(roles, RolesJson),
                json => JsonSerializer.Deserialize<List<RoleAssignment>>(json, RolesJson)!,
                new ValueComparer<IReadOnlyList<RoleAssignment>>(
                    (a, b) => a!.SequenceEqual(b!), roles => roles.Aggregate(0, (hash, r) => HashCode.Combine(hash, r)), roles => roles.ToList()));
            user.Property<uint>("xmin").IsRowVersion();

            // One account per e-mail, phone, employee ID and Keycloak subject within a tenant.
            user.HasIndex(u => new { u.TenantId, u.Email }).IsUnique().HasFilter("email IS NOT NULL");
            user.HasIndex(u => new { u.TenantId, u.Phone }).IsUnique().HasFilter("phone IS NOT NULL");
            user.HasIndex(u => new { u.TenantId, u.EmployeeId }).IsUnique().HasFilter("employee_id IS NOT NULL");
            user.HasIndex(u => u.Subject).IsUnique().HasFilter("subject IS NOT NULL");
            user.Ignore(u => u.DisplayName);
            user.Ignore(u => u.RequiresMfa);
            user.Ignore(u => u.DomainEvents);
            user.Ignore(u => u.AggregateKey);
        });

        modelBuilder.Entity<Invitation>(invitation =>
        {
            invitation.ToTable("invitations");
            invitation.HasKey(i => i.Id);
            invitation.Property(i => i.Id).HasConversion(id => id.Value, value => new InvitationId(value)).ValueGeneratedNever();
            invitation.Property(i => i.UserId).HasConversion(id => id.Value, value => new UserId(value));
            invitation.Property(i => i.Channel).HasConversion<string>().HasMaxLength(10);
            invitation.Property(i => i.TokenHash).HasMaxLength(64).IsFixedLength();
            invitation.Property<uint>("xmin").IsRowVersion();
            invitation.HasIndex(i => i.TokenHash).IsUnique();
            invitation.HasIndex(i => i.UserId);
            invitation.HasOne<UserAccount>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Restrict);
            invitation.Ignore(i => i.DomainEvents);
            invitation.Ignore(i => i.AggregateKey);
        });

        modelBuilder.Entity<SignInFailure>(failure =>
        {
            failure.ToTable("sign_in_failures");
            failure.HasKey(f => f.Id);
            failure.Property(f => f.Id).HasConversion(id => id.Value, value => new SignInFailureId(value)).ValueGeneratedNever();
            failure.Property(f => f.UserId).HasConversion(id => id.Value, value => new UserId(value));
            failure.Property(f => f.Reason).HasMaxLength(SignInFailure.ReasonMaxLength);
            failure.Property(f => f.IpAddress).HasMaxLength(45);
            failure.HasIndex(f => new { f.UserId, f.OccurredAt });
            failure.HasOne<UserAccount>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Restrict);
            failure.Ignore(f => f.Locked);
            failure.Ignore(f => f.DomainEvents);
            failure.Ignore(f => f.AggregateKey);
        });

        // Platform bookkeeping of the importers (no tenant data): see deploy/database/global-reference-tables.txt.
        modelBuilder.Entity<ImportCursorRow>(cursor =>
        {
            cursor.ToTable("import_cursors");
            cursor.HasKey(c => c.Source);
            cursor.Property(c => c.Source).HasMaxLength(60);
        });
    }
}
