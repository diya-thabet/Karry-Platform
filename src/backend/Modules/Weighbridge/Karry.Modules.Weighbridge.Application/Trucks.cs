using System.Data.Common;
using System.Globalization;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Weighbridge.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using NodaTime;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Weighbridge.Application;

/// <summary>Weighbridge permissions (Phase 3 sub-phases §3.4): trucks and haulers in P3.2.</summary>
public static class WeighbridgePermissions
{
    /// <summary>See trucks and haulers (the desk searches plates).</summary>
    public const string TrucksRead = "weighbridge.trucks.read";

    /// <summary>Register and change trucks and haulers; create an unknown truck at the desk (flagged).</summary>
    public const string TrucksManage = "weighbridge.trucks.manage";

    /// <summary>
    /// Verify a truck created at the desk (closes its review flag). Not the weighmaster's: the person who met an unknown
    /// plate does not clear their own flag.
    /// </summary>
    public const string TrucksVerify = "weighbridge.trucks.verify";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(TrucksRead, ScopeKind.Tenant, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Commercial, Roles.Controller, Roles.SiteManager, Roles.Weighmaster),
        new(TrucksManage, ScopeKind.Tenant, privileged: false, Roles.TenantAdmin, Roles.Commercial, Roles.Weighmaster),
        new(TrucksVerify, ScopeKind.Tenant, privileged: false, Roles.TenantAdmin, Roles.Commercial, Roles.SiteManager, Roles.Controller),
    ];
}

/// <summary>The rule an unknown truck raises (detection-rules.md).</summary>
public static class WeighbridgeRules
{
    /// <summary>A truck created at the desk for an unknown plate: open until someone verifies it (US-P3-SAL-04 AC2).</summary>
    public const string UnverifiedTruck = IntegrityRules.UnverifiedTruck;

    /// <summary>The flag subject type of a truck.</summary>
    public const string TruckSubject = "truck";
}

public sealed record HaulerInput(string? Name, string? Phone, string? Status);

/// <summary>A registered tare: mass in kg, the day it was established and how (<c>weighed</c> or <c>declared</c>).</summary>
public sealed record TareInput(decimal? Kg, DateOnly? On, string? Method);

/// <summary>A truck as the portal or an import sends it; the plate is given once, at registration.</summary>
public sealed record TruckInput(string? Plate, Guid? HaulerId, TareInput? Tare, decimal? MaxPayloadKg, string? Axles);

/// <summary>A plate nobody knew, at the desk: only the plate, and the hauler when the driver says.</summary>
public sealed record UnknownTruckInput(string? Plate, Guid? HaulerId);

public interface IHaulerStore
{
    Task<Hauler?> FindAsync(HaulerId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Hauler>> ListAsync(bool includeArchived, CancellationToken cancellationToken);

    /// <summary>Saves with its events; false when another hauler has the same name or it changed since it was loaded.</summary>
    Task<bool> TrySaveAsync(Hauler hauler, CancellationToken cancellationToken);
}

public interface ITruckStore
{
    Task<Truck?> FindAsync(TruckId id, CancellationToken cancellationToken);

    /// <summary>Trucks whose plate key contains <paramref name="plateKey"/> (any part of the plate), best match first.</summary>
    Task<IReadOnlyList<Truck>> SearchAsync(string? plateKey, TruckStatus? status, Guid? haulerId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Saves with its events, then runs <paramref name="alsoInTransaction"/> on the same connection and transaction (the
    /// review flag), all or nothing; false when the plate is taken or the truck changed since it was loaded.
    /// </summary>
    Task<bool> TrySaveAsync(Truck truck, Func<DbConnection, DbTransaction, Task> alsoInTransaction, CancellationToken cancellationToken);
}

/// <summary>Haulers: the companies whose trucks cross the scale.</summary>
public sealed class HaulerOperations(IHaulerStore store, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("weighbridge.hauler_not_found", "No such hauler in this company.");

    public static readonly Error NameTaken = Error.Conflict("weighbridge.hauler_name_taken", "Another hauler of the company already has this name.");

    public static readonly Error StatusInvalid = Error.Validation("weighbridge.hauler_status_invalid", "The status is active or archived.");

    public async Task<IReadOnlyList<Hauler>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        [.. (await store.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false)).OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)];

    public async Task<Result<Hauler>> GetAsync(HaulerId id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } hauler ? hauler : NotFound;

    public async Task<Result<Hauler>> CreateAsync(HaulerInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = TruckRules.HaulerName(input.Name);
        var phone = TruckRules.Phone(input.Phone);
        if (!name.IsSuccess || !phone.IsSuccess)
        {
            return name.IsSuccess ? phone.Error! : name.Error!;
        }

        var hauler = Hauler.Create(clock, Tenant, name.Value, phone.Value, caller.Subject);
        return await store.TrySaveAsync(hauler, cancellationToken).ConfigureAwait(false) ? hauler : NameTaken;
    }

    public async Task<Result<Hauler>> ChangeAsync(HaulerId id, HaulerInput input, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hauler = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!hauler.IsSuccess)
        {
            return hauler;
        }

        if (!RecordVersions.Matches(hauler.Value.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var name = TruckRules.HaulerName(input.Name);
        var phone = TruckRules.Phone(input.Phone);
        if (!name.IsSuccess || !phone.IsSuccess)
        {
            return name.IsSuccess ? phone.Error! : name.Error!;
        }

        RecordStatus? status = (input.Status ?? "active") switch
        {
            "active" => RecordStatus.Active,
            "archived" => RecordStatus.Archived,
            _ => null,
        };
        if (status is null)
        {
            return StatusInvalid;
        }

        if (hauler.Value.Change(clock, name.Value, phone.Value, status.Value, caller.Subject) && !await store.TrySaveAsync(hauler.Value, cancellationToken).ConfigureAwait(false))
        {
            return NameTaken;
        }

        return hauler;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Weighbridge needs a tenant.");
}

/// <summary>US-P3-SAL-04: the trucks that cross the scale, found by plate; unknown plates created at the desk are flagged.</summary>
public sealed class TruckOperations(
    ITruckStore store, IHaulerStore haulers, IIntegrityFlags integrity, ITenantDirectory tenants, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public const int MaxResults = 50;

    public static readonly Error NotFound = Error.NotFound("weighbridge.truck_not_found", "No such truck in this company.");

    public static readonly Error PlateTaken = Error.Conflict(
        "weighbridge.truck_plate_taken", "The company already has a truck with this plate (written with other spaces, dashes or case, perhaps).");

    public static readonly Error HaulerUnknown = Error.Validation("weighbridge.truck_hauler_unknown", "The hauler is not an active hauler of this company.");

    public static readonly Error StatusInvalid = Error.Validation("weighbridge.truck_status_invalid", "The status is active, unverified or archived.");

    /// <summary>Trucks by any part of the plate (spaces, dashes and case ignored), at most <see cref="MaxResults"/>.</summary>
    public async Task<Result<IReadOnlyList<Truck>>> SearchAsync(string? plate, string? status, Guid? haulerId, CancellationToken cancellationToken)
    {
        TruckStatus? wanted = status switch
        {
            null or "" => null,
            "active" => TruckStatus.Active,
            "unverified" => TruckStatus.Unverified,
            "archived" => TruckStatus.Archived,
            _ => (TruckStatus?)(TruckStatus)(-1),
        };
        if (wanted == (TruckStatus)(-1))
        {
            return StatusInvalid;
        }

        var key = string.IsNullOrWhiteSpace(plate) ? null : TruckRules.Key(plate);
        return Result.Success(await store.SearchAsync(key, wanted, haulerId, MaxResults, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<Truck>> GetAsync(TruckId id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } truck ? truck : NotFound;

    /// <summary>A truck registered with its details (portal): active.</summary>
    public async Task<Result<Truck>> RegisterAsync(TruckInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plate = TruckRules.Plate(input.Plate);
        if (!plate.IsSuccess)
        {
            return plate.Error;
        }

        var details = await DetailsAsync(input, cancellationToken).ConfigureAwait(false);
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        var truck = Truck.Register(clock, Tenant, plate.Value, details.Value, caller.Subject);
        return await SaveAsync(truck, cancellationToken).ConfigureAwait(false) ? truck : PlateTaken;
    }

    /// <summary>A plate nobody knew, at the desk (US-P3-SAL-04 AC2, TC-P3-WB-017): unverified, with a review flag in the same transaction.</summary>
    public async Task<Result<Truck>> CreateUnknownAsync(UnknownTruckInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plate = TruckRules.Plate(input.Plate);
        if (!plate.IsSuccess)
        {
            return plate.Error;
        }

        if (input.HaulerId is { } haulerId && await haulers.FindAsync(new HaulerId(haulerId), cancellationToken).ConfigureAwait(false) is not { Status: RecordStatus.Active })
        {
            return HaulerUnknown;
        }

        var truck = Truck.CreateUnknown(clock, Tenant, plate.Value, input.HaulerId, caller.Subject);
        return await SaveAsync(truck, cancellationToken).ConfigureAwait(false) ? truck : PlateTaken;
    }

    public async Task<Result<Truck>> ChangeAsync(TruckId id, TruckInput input, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var truck = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!truck.IsSuccess)
        {
            return truck;
        }

        if (!RecordVersions.Matches(truck.Value.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var details = await DetailsAsync(input, cancellationToken, truck.Value.HaulerId).ConfigureAwait(false);
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        return truck.Value.Change(clock, details.Value, caller.Subject) && !await SaveAsync(truck.Value, cancellationToken).ConfigureAwait(false)
            ? RecordVersions.Conflict
            : truck;
    }

    public Task<Result<Truck>> VerifyAsync(TruckId id, CancellationToken cancellationToken) => TransitionAsync(id, t => t.Verify(clock, caller.Subject), cancellationToken);

    public Task<Result<Truck>> ArchiveAsync(TruckId id, CancellationToken cancellationToken) => TransitionAsync(id, t => t.Archive(clock, caller.Subject), cancellationToken);

    public Task<Result<Truck>> RestoreAsync(TruckId id, CancellationToken cancellationToken) => TransitionAsync(id, t => t.Restore(clock, caller.Subject), cancellationToken);

    private async Task<Result<Truck>> TransitionAsync(TruckId id, Func<Truck, bool> transition, CancellationToken cancellationToken)
    {
        var truck = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (truck.IsSuccess && transition(truck.Value) && !await SaveAsync(truck.Value, cancellationToken).ConfigureAwait(false))
        {
            return RecordVersions.Conflict;
        }

        return truck;
    }

    /// <summary>
    /// Saves the truck and, in the same transaction, the review flag as its status says: open while unverified, closed
    /// (re-evaluated) once verified or archived. The flag API opens or closes only what changed.
    /// </summary>
    private Task<bool> SaveAsync(Truck truck, CancellationToken cancellationToken) =>
        store.TrySaveAsync(truck, async (connection, transaction) =>
        {
            IReadOnlyList<Finding> findings = truck.Status == TruckStatus.Unverified
                ? [new Finding("unverified", [], [], new Dictionary<string, string>(StringComparer.Ordinal) { ["plate"] = truck.Plate })]
                : [];
            await integrity.SyncAsync(connection, transaction, new FlagSubject(WeighbridgeRules.TruckSubject, truck.Id.Value), WeighbridgeRules.UnverifiedTruck,
                null, findings, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Checked details; <paramref name="keptHauler"/> (the truck's current hauler) is not checked again, archived or not.</summary>
    private async Task<Result<TruckDetails>> DetailsAsync(TruckInput input, CancellationToken cancellationToken, Guid? keptHauler = null)
    {
        if (input.HaulerId is { } haulerId && haulerId != keptHauler &&
            await haulers.FindAsync(new HaulerId(haulerId), cancellationToken).ConfigureAwait(false) is not { Status: RecordStatus.Active })
        {
            return HaulerUnknown;
        }

        var tare = TruckRules.Tare(input.Tare?.Kg, input.Tare?.On, input.Tare?.Method, await TodayAsync(cancellationToken).ConfigureAwait(false));
        var payload = TruckRules.Payload(input.MaxPayloadKg);
        var axles = TruckRules.Axles(input.Axles);
        if (!tare.IsSuccess)
        {
            return tare.Error;
        }

        if (!payload.IsSuccess)
        {
            return payload.Error;
        }

        return axles.IsSuccess ? new TruckDetails(input.HaulerId, tare.Value, payload.Value, axles.Value) : axles.Error;
    }

    /// <summary>Today on the company's clock (a tare is not established in the future).</summary>
    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var zone = (await tenants.FindAsync(Tenant, cancellationToken).ConfigureAwait(false))?.TimeZone ?? "UTC";
        var date = Instant.FromDateTimeOffset(clock.GetUtcNow()).InZone(DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) ?? DateTimeZone.Utc).Date;
        return DateOnly.Parse(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Weighbridge needs a tenant.");
}
