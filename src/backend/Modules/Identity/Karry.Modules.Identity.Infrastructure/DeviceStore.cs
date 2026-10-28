using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>An assertion ID a tablet used (replay protection, ADR-0028 §3); kept until the assertion expires.</summary>
public sealed class DeviceAssertionRow
{
    public required Guid TenantId { get; set; }

    public required Guid DeviceId { get; set; }

    public required string AssertionId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class DeviceStore(IdentityDbContext db, ITenantContext tenantContext) : IDeviceStore, IDeviceDirectory
{
    public void Add(DeviceEnrollment enrollment) => db.DeviceEnrollments.Add(enrollment);

    public void Add(Device device) => db.Devices.Add(device);

    public Task<DeviceEnrollment?> FindEnrollmentAsync(string codeHash, CancellationToken cancellationToken) =>
        db.DeviceEnrollments.SingleOrDefaultAsync(e => e.CodeHash == codeHash, cancellationToken);

    public Task<Device?> FindDeviceAsync(DeviceId id, CancellationToken cancellationToken) =>
        db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken cancellationToken) =>
        await db.Devices.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<UserAccount?> FindByEmployeeIdAsync(string employeeId, CancellationToken cancellationToken)
    {
        if (employeeId.Length == 0)
        {
            return null;
        }

        var exact = await db.Users.SingleOrDefaultAsync(u => u.EmployeeId == employeeId, cancellationToken).ConfigureAwait(false);
        if (exact is not null)
        {
            return exact;
        }

        // Typed in another case on the tablet: accepted only when it designates one user.
        // ILIKE with LIKE's special characters escaped: a case-insensitive equality, not a pattern.
        var literal = employeeId.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
        var matches = await db.Users.Where(u => u.EmployeeId != null && EF.Functions.ILike(u.EmployeeId, literal)).Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<Result<bool>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Error.Conflict("identity.concurrent_change", "Someone else changed this at the same time. Try again.");
        }
    }

    public async Task<DeviceKey?> FindAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var id = new DeviceId(deviceId);
        var device = await db.Devices.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new { d.SiteId, d.PublicKeyX, d.PublicKeyY, d.Status })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return device is null ? null : new DeviceKey(deviceId, device.SiteId, device.PublicKeyX, device.PublicKeyY, device.Status == DeviceStatus.Revoked);
    }

    /// <summary>
    /// Inserts the assertion ID; the primary key refuses a second use. The device's expired IDs are removed afterwards,
    /// so the table stays the size of five minutes of traffic.
    /// </summary>
    public async Task<bool> RememberAssertionAsync(Guid deviceId, string assertionId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Current?.Value ?? throw new InvalidOperationException("No tenant.");
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO identity.device_assertions (tenant_id, device_id, assertion_id, expires_at)
            VALUES ({tenant}, {deviceId}, {assertionId}, {expiresAt}) ON CONFLICT DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM identity.device_assertions WHERE device_id = {deviceId} AND expires_at < now()", cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }
}
