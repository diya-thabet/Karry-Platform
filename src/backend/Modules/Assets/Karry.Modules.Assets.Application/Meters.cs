using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Assets.Application;

/// <summary>A new meter: <c>Kind</c> hours or km, the value shown when installed (default 0) and the date (default the company's today).</summary>
public sealed record MeterInput(string? Kind, decimal? InitialReading, DateOnly? InstalledOn);

/// <summary>What a meter shows; <c>ReadAt</c> defaults to now.</summary>
public sealed record ReadingInput(decimal? Value, DateTimeOffset? ReadAt);

/// <summary>A page of readings, newest first; the next page starts after <c>NextCursor</c> (a reading ID).</summary>
public sealed record ReadingPage(IReadOnlyList<MeterReading> Items, Guid? NextCursor);

/// <summary>Persistence port of meters and their readings (the caller's unit of work, RLS).</summary>
public interface IMeterStore
{
    Task<IReadOnlyList<Meter>> ListAsync(AssetId asset, CancellationToken cancellationToken);

    Task<Meter?> FindAsync(MeterId id, CancellationToken cancellationToken);

    /// <summary>Tracks a new meter and its pending readings, saved with the next save of the unit of work.</summary>
    void Add(Meter meter);

    /// <summary>Saves the meter with its pending readings and events; false when someone else changed it meanwhile.</summary>
    Task<bool> TrySaveAsync(Meter meter, CancellationToken cancellationToken);

    /// <summary>The meters of these assets (tracked, for an import).</summary>
    Task<IReadOnlyList<Meter>> ForAssetsAsync(IReadOnlyCollection<AssetId> assets, CancellationToken cancellationToken);

    /// <summary>Tracks the new readings of a meter, saved with the next save of the unit of work.</summary>
    void Stage(Meter meter);

    /// <summary>Up to <paramref name="take"/> readings, newest first (by reading time), after the reading <paramref name="after"/>.</summary>
    Task<IReadOnlyList<MeterReading>> ReadingsAsync(MeterId meter, Guid? after, int take, CancellationToken cancellationToken);
}

/// <summary>
/// US-P1-AST-03: the meters of an asset and their readings. The history is append-only (TC-P1-AST-002): a reading is
/// never changed or removed, each one is in the company's ledger, and a meter never goes back. Readings come from the
/// people who manage the asset in P1 and from the field app in Phase 2.
/// </summary>
public sealed class MeterOperations(IMeterStore store, AssetOperations assets, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public const int DefaultLimit = 50;

    public static readonly Error NotFound = Error.NotFound("assets.meter_not_found", "No such meter on this asset.");

    public static readonly Error Changed = Error.Conflict("assets.meter_changed", "The meter changed meanwhile (another reading?); read it again and retry.");

    /// <summary>Validates a new meter: a known kind, a value in range and an installation date from 1950 to today.</summary>
    public static Result<(MeterKind Kind, decimal Initial, DateOnly On)> Validate(MeterInput input, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(input);
        var kind = MeterRules.Kind(input.Kind);
        if (!kind.IsSuccess)
        {
            return kind.Error;
        }

        var initial = input.InitialReading ?? 0m;
        if (MeterRules.Value(initial) is { } invalid)
        {
            return invalid;
        }

        var on = input.InstalledOn ?? today;
        if (on.Year < AssetRules.FirstYear || on > today)
        {
            return MeterRules.InstalledOnInvalid(today);
        }

        return (kind.Value, initial, on);
    }

    public async Task<Result<IReadOnlyList<Meter>>> ListAsync(AssetId assetId, CancellationToken cancellationToken)
    {
        var asset = await assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        return asset.IsSuccess ? Result.Success(await store.ListAsync(assetId, cancellationToken).ConfigureAwait(false)) : asset.Error;
    }

    public async Task<Result<Meter>> AddAsync(AssetId assetId, MeterInput input, CancellationToken cancellationToken)
    {
        var asset = await CountingAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (!asset.IsSuccess)
        {
            return asset.Error;
        }

        var valid = Validate(input, await assets.TodayAsync(cancellationToken).ConfigureAwait(false));
        if (!valid.IsSuccess)
        {
            return valid.Error;
        }

        var meter = Meter.Add(clock, Tenant, assetId, valid.Value.Kind, valid.Value.Initial, valid.Value.On, caller.Subject);
        store.Add(meter);
        await store.TrySaveAsync(meter, cancellationToken).ConfigureAwait(false);
        return meter;
    }

    public async Task<Result<Meter>> SetStatusAsync(AssetId assetId, MeterId meterId, string? status, CancellationToken cancellationToken)
    {
        var meter = await ManagedMeterAsync(assetId, meterId, cancellationToken).ConfigureAwait(false);
        if (!meter.IsSuccess)
        {
            return meter;
        }

        var parsed = MeterRules.Status(status);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var changed = meter.Value.SetStatus(clock, parsed.Value, caller.Subject);
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        return !changed.Value || await store.TrySaveAsync(meter.Value, cancellationToken).ConfigureAwait(false) ? meter : Changed;
    }

    public async Task<Result<MeterReading>> RecordAsync(AssetId assetId, MeterId meterId, ReadingInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var meter = await ManagedMeterAsync(assetId, meterId, cancellationToken).ConfigureAwait(false);
        if (!meter.IsSuccess)
        {
            return meter.Error;
        }

        if (input.Value is not { } value)
        {
            return MeterRules.ValueInvalid;
        }

        var reading = meter.Value.Record(clock, value, input.ReadAt ?? clock.GetUtcNow(), caller.Subject);
        if (!reading.IsSuccess)
        {
            return reading;
        }

        return await store.TrySaveAsync(meter.Value, cancellationToken).ConfigureAwait(false) ? reading : Changed;
    }

    public async Task<Result<ReadingPage>> ReadingsAsync(AssetId assetId, MeterId meterId, Guid? cursor, int? limit, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > AssetOperations.MaxLimit)
        {
            return AssetOperations.LimitInvalid;
        }

        var asset = await assets.GetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (!asset.IsSuccess)
        {
            return asset.Error;
        }

        if (await store.FindAsync(meterId, cancellationToken).ConfigureAwait(false) is not { } meter || meter.AssetId != assetId)
        {
            return NotFound;
        }

        var found = await store.ReadingsAsync(meterId, cursor, take + 1, cancellationToken).ConfigureAwait(false);
        return found.Count > take ? new ReadingPage([.. found.Take(take)], found[take - 1].Id) : new ReadingPage(found, null);
    }

    /// <summary>A meter of an asset the caller manages, on an asset that is not disposed.</summary>
    private async Task<Result<Meter>> ManagedMeterAsync(AssetId assetId, MeterId meterId, CancellationToken cancellationToken)
    {
        var asset = await CountingAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (!asset.IsSuccess)
        {
            return asset.Error;
        }

        return await store.FindAsync(meterId, cancellationToken).ConfigureAwait(false) is { } meter && meter.AssetId == assetId ? meter : NotFound;
    }

    private async Task<Result<Asset>> CountingAssetAsync(AssetId assetId, CancellationToken cancellationToken)
    {
        var asset = await assets.ManagedAsync(assetId, cancellationToken).ConfigureAwait(false);
        return !asset.IsSuccess || asset.Value.Status != AssetStatus.Disposed ? asset : AssetRules.Disposed;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Assets need a tenant.");
}
