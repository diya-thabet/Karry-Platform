using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>Position of an importer (e.g. Keycloak sign-in failures): platform data, one row per source.</summary>
public sealed class ImportCursorRow
{
    public required string Source { get; set; }

    public DateTimeOffset Position { get; set; }
}

/// <summary>
/// Schema <c>identity</c>: users, invitations, sign-in failures, custom roles and default-role field levels
/// (tenant-owned, row-level security) and importer cursors.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "identity";

    private static readonly JsonSerializerOptions RolesJson = new(JsonSerializerDefaults.Web);

    /// <summary>Field levels as <c>{"field": "masked"}</c> (lower-case names, like the API and the ledger).</summary>
    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IReadOnlyDictionary<string, FieldAccess>, string> FieldLevelsConverter = new(
        levels => JsonSerializer.Serialize(levels.ToDictionary(l => l.Key, l => l.Value.ToString().ToLowerInvariant()), RolesJson),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, RolesJson)!
            .ToDictionary(l => l.Key, l => Enum.Parse<FieldAccess>(l.Value, ignoreCase: true), StringComparer.Ordinal));

    private static readonly ValueComparer<IReadOnlyDictionary<string, FieldAccess>> FieldLevelsComparer = new(
        (a, b) => a!.Count == b!.Count && a.All(l => b.ContainsKey(l.Key) && b[l.Key] == l.Value),
        levels => levels.OrderBy(l => l.Key, StringComparer.Ordinal).Aggregate(0, (hash, l) => HashCode.Combine(hash, l.Key, l.Value)),
        levels => levels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal));

    public override string Schema => SchemaName;

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<SignInFailure> SignInFailures => Set<SignInFailure>();

    public DbSet<ImportCursorRow> ImportCursors => Set<ImportCursorRow>();

    public DbSet<CustomRole> CustomRoles => Set<CustomRole>();

    public DbSet<DefaultRoleSettings> DefaultRoleSettings => Set<DefaultRoleSettings>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceEnrollment> DeviceEnrollments => Set<DeviceEnrollment>();

    public DbSet<DeviceAssertionRow> DeviceAssertions => Set<DeviceAssertionRow>();

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

        modelBuilder.Entity<CustomRole>(role =>
        {
            role.ToTable("roles");
            role.HasKey(r => r.Id);
            role.Property(r => r.Id).HasConversion(id => id.Value, value => new RoleId(value)).ValueGeneratedNever();
            role.Property(r => r.Code).HasMaxLength(60);
            role.Property(r => r.Name).HasMaxLength(CustomRole.NameMaxLength);
            role.Property(r => r.Description).HasMaxLength(CustomRole.DescriptionMaxLength);
            role.Property(r => r.BasedOn).HasMaxLength(60);
            role.Property(r => r.Permissions).HasColumnType("jsonb").HasConversion(
                permissions => JsonSerializer.Serialize(permissions, RolesJson),
                json => JsonSerializer.Deserialize<List<string>>(json, RolesJson)!,
                new ValueComparer<IReadOnlyList<string>>(
                    (a, b) => a!.SequenceEqual(b!), list => list.Aggregate(0, (hash, p) => HashCode.Combine(hash, p)), list => list.ToList()));
            role.Property(r => r.FieldLevels).HasColumnType("jsonb").HasConversion(FieldLevelsConverter, FieldLevelsComparer);
            role.Property<uint>("xmin").IsRowVersion();
            role.HasIndex(r => new { r.TenantId, r.Code }).IsUnique();
            role.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
            role.Ignore(r => r.Content);
            role.Ignore(r => r.DomainEvents);
            role.Ignore(r => r.AggregateKey);
        });

        modelBuilder.Entity<DefaultRoleSettings>(settings =>
        {
            settings.ToTable("default_role_settings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).HasConversion(id => id.Value, value => new DefaultRoleSettingsId(value)).ValueGeneratedNever();
            settings.Property(s => s.Role).HasMaxLength(60);
            settings.Property(s => s.FieldLevels).HasColumnType("jsonb").HasConversion(FieldLevelsConverter, FieldLevelsComparer);
            settings.Property<uint>("xmin").IsRowVersion();
            settings.HasIndex(s => new { s.TenantId, s.Role }).IsUnique();
            settings.Ignore(s => s.DomainEvents);
            settings.Ignore(s => s.AggregateKey);
        });

        modelBuilder.Entity<DeviceEnrollment>(enrollment =>
        {
            enrollment.ToTable("device_enrollments");
            enrollment.HasKey(e => e.Id);
            enrollment.Property(e => e.Id).HasConversion(id => id.Value, value => new DeviceEnrollmentId(value)).ValueGeneratedNever();
            enrollment.Property(e => e.CodeHash).HasMaxLength(64).IsFixedLength();
            enrollment.Property(e => e.CreatedBy).HasMaxLength(64);
            enrollment.Property(e => e.DeviceId).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DeviceId, Guid>(id => id.Value, value => new DeviceId(value)));
            enrollment.Property<uint>("xmin").IsRowVersion(); // the same code used by two tablets at once: one wins
            enrollment.HasIndex(e => new { e.TenantId, e.CodeHash }).IsUnique();
            enrollment.Ignore(e => e.DomainEvents);
            enrollment.Ignore(e => e.AggregateKey);
        });

        modelBuilder.Entity<Device>(device =>
        {
            device.ToTable("devices");
            device.HasKey(d => d.Id);
            device.Property(d => d.Id).HasConversion(id => id.Value, value => new DeviceId(value)).ValueGeneratedNever();
            device.Property(d => d.Name).HasMaxLength(Device.NameMaxLength);
            device.Property(d => d.Model).HasMaxLength(Device.DetailMaxLength);
            device.Property(d => d.Platform).HasMaxLength(Device.DetailMaxLength);
            device.Property(d => d.AppVersion).HasMaxLength(Device.AppVersionMaxLength);
            device.Property(d => d.Status).HasConversion<string>().HasMaxLength(10);
            device.Property(d => d.RevokedBy).HasMaxLength(64);
            device.Property<uint>("xmin").IsRowVersion();
            device.HasIndex(d => new { d.TenantId, d.SiteId });
            device.Ignore(d => d.DomainEvents);
            device.Ignore(d => d.AggregateKey);
        });

        modelBuilder.Entity<DeviceAssertionRow>(assertion =>
        {
            assertion.ToTable("device_assertions");
            assertion.HasKey(a => new { a.DeviceId, a.AssertionId });
            assertion.Property(a => a.AssertionId).HasMaxLength(64);
            assertion.HasIndex(a => a.ExpiresAt);
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
