using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Weighbridge.Domain;

public readonly record struct HaulerId(Guid Value) : IEntityId<HaulerId>
{
    public static HaulerId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

public readonly record struct TruckId(Guid Value) : IEntityId<TruckId>
{
    public static TruckId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

public enum RecordStatus
{
    Active = 1,

    /// <summary>No longer in use: kept for the tickets that name it, out of the default lists.</summary>
    Archived = 2,
}

/// <summary>How a registered tare was obtained (US-P3-SAL-04 AC1).</summary>
public enum TareMethod
{
    /// <summary>Weighed empty on this company's weighbridge.</summary>
    Weighed = 1,

    /// <summary>From the registration document or the manufacturer.</summary>
    Declared = 2,
}

public enum TruckStatus
{
    Active = 1,

    /// <summary>Created at the desk for a plate nobody knew (US-P3-SAL-04 AC2): usable, flagged until someone verifies it.</summary>
    Unverified = 2,

    Archived = 3,
}

/// <summary>The truck's registered empty weight in kg, the day it was established and how (explicit unit: kg).</summary>
public sealed record RegisteredTare(decimal Kg, DateOnly On, TareMethod Method);

/// <summary>What a truck is, as entered and as stored in the ledger. Masses in kg.</summary>
public sealed record TruckDetails(Guid? HaulerId, RegisteredTare? Tare, decimal? MaxPayloadKg, string? Axles);

[EventType("weighbridge.hauler_created.v1")]
[AuditSubject("hauler", nameof(HaulerId))]
public sealed record HaulerCreated(Guid HaulerId, string Name, string? Phone, string? Actor) : DomainEvent;

[EventType("weighbridge.hauler_changed.v1")]
[AuditSubject("hauler", nameof(HaulerId))]
public sealed record HaulerChanged(Guid HaulerId, string Name, string? Phone, string Status, string? Actor) : DomainEvent;

[EventType("weighbridge.truck_created.v1")]
[AuditSubject("truck", nameof(TruckId))]
public sealed record TruckCreated(Guid TruckId, string Plate, TruckDetails Truck, string Status, string? Actor) : DomainEvent;

[EventType("weighbridge.truck_changed.v1")]
[AuditSubject("truck", nameof(TruckId))]
public sealed record TruckChanged(Guid TruckId, string Plate, TruckDetails Before, TruckDetails After, string? Actor) : DomainEvent;

[EventType("weighbridge.truck_verified.v1")]
[AuditSubject("truck", nameof(TruckId))]
public sealed record TruckVerified(Guid TruckId, string Plate, string? Actor) : DomainEvent;

[EventType("weighbridge.truck_archived.v1")]
[AuditSubject("truck", nameof(TruckId))]
public sealed record TruckArchived(Guid TruckId, string Plate, string? Actor) : DomainEvent;

[EventType("weighbridge.truck_restored.v1")]
[AuditSubject("truck", nameof(TruckId))]
public sealed record TruckRestored(Guid TruckId, string Plate, string? Actor) : DomainEvent;

/// <summary>A haulage company whose trucks cross the scale (US-P3-SAL-04 AC1). Tenant-owned; archived, never deleted.</summary>
public sealed class Hauler : AggregateRoot<HaulerId>, ITenantOwned
{
    private Hauler(HaulerId id, TenantId tenantId, string name, string? phone, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        Name = name;
        NameKey = TruckRules.Key(name);
        Phone = phone;
        Status = RecordStatus.Active;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Hauler()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>The name without case, spaces or punctuation: unique in the company ("Trans-Nord" = "TRANS NORD").</summary>
    public string NameKey { get; private set; } = null!;

    public string? Phone { get; private set; }

    public RecordStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public uint Version { get; private set; }

    public static Hauler Create(TimeProvider clock, TenantId tenant, string name, string? phone, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var at = clock.GetUtcNow();
        var hauler = new Hauler(EntityId.New<HaulerId>(clock), tenant, name, phone, at);
        hauler.Raise(new HaulerCreated(hauler.Id.Value, name, phone, actor) { OccurredAt = at });
        return hauler;
    }

    /// <summary>New name, phone and status; false when nothing changed.</summary>
    public bool Change(TimeProvider clock, string name, string? phone, RecordStatus status, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Name == name && Phone == phone && Status == status)
        {
            return false;
        }

        Name = name;
        NameKey = TruckRules.Key(name);
        Phone = phone;
        Status = status;
        ChangedAt = clock.GetUtcNow();
        Raise(new HaulerChanged(Id.Value, name, phone, status.ToString().ToLowerInvariant(), actor) { OccurredAt = ChangedAt });
        return true;
    }
}

/// <summary>
/// A truck that crosses the scale (US-P3-SAL-04, ADR-0042): found by its plate at the desk, its registered tare compared
/// with weighed tares (RULE-WB-01) and its legal payload with nets (RULE-WB-02). Tenant-owned; never deleted.
/// </summary>
public sealed class Truck : AggregateRoot<TruckId>, ITenantOwned
{
    private Truck(TruckId id, TenantId tenantId, string plate, TruckDetails details, TruckStatus status, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        Plate = plate;
        PlateKey = TruckRules.Key(plate);
        Apply(details);
        Status = status;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Truck()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    /// <summary>The plate as people write it (capitals, single spaces), e.g. "123 TU 4567".</summary>
    public string Plate { get; private set; } = null!;

    /// <summary>The plate with only letters and digits, upper case: unique in the company, what the desk searches (TC-P3-WB-016).</summary>
    public string PlateKey { get; private set; } = null!;

    public Guid? HaulerId { get; private set; }

    public decimal? TareKg { get; private set; }

    public DateOnly? TareOn { get; private set; }

    public TareMethod? TareMethod { get; private set; }

    public decimal? MaxPayloadKg { get; private set; }

    /// <summary>Axle configuration, e.g. "2", "3" or "2+3" (tractor + trailer).</summary>
    public string? Axles { get; private set; }

    public TruckStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public uint Version { get; private set; }

    public RegisteredTare? Tare => TareKg is { } kg && TareOn is { } on && TareMethod is { } method ? new RegisteredTare(kg, on, method) : null;

    public TruckDetails Details => new(HaulerId, Tare, MaxPayloadKg, Axles);

    /// <summary>A truck registered with its details (portal, import): active at once.</summary>
    public static Truck Register(TimeProvider clock, TenantId tenant, string plate, TruckDetails details, string? actor) =>
        Create(clock, tenant, plate, details, TruckStatus.Active, actor);

    /// <summary>A plate nobody knew, created at the desk so the truck can be weighed (US-P3-SAL-04 AC2): unverified.</summary>
    public static Truck CreateUnknown(TimeProvider clock, TenantId tenant, string plate, Guid? haulerId, string? actor) =>
        Create(clock, tenant, plate, new TruckDetails(haulerId, null, null, null), TruckStatus.Unverified, actor);

    private static Truck Create(TimeProvider clock, TenantId tenant, string plate, TruckDetails details, TruckStatus status, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var at = clock.GetUtcNow();
        var truck = new Truck(EntityId.New<TruckId>(clock), tenant, plate, details, status, at);
        truck.Raise(new TruckCreated(truck.Id.Value, plate, details, status.ToString().ToLowerInvariant(), actor) { OccurredAt = at });
        return truck;
    }

    /// <summary>New details; false when nothing changed. The plate never changes (a new plate is a new truck).</summary>
    public bool Change(TimeProvider clock, TruckDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var before = Details;
        if (before == details)
        {
            return false;
        }

        Apply(details);
        ChangedAt = clock.GetUtcNow();
        Raise(new TruckChanged(Id.Value, Plate, before, details, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Someone checked an unverified truck (its review flag closes); false when it was not unverified.</summary>
    public bool Verify(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != TruckStatus.Unverified)
        {
            return false;
        }

        Status = TruckStatus.Active;
        ChangedAt = clock.GetUtcNow();
        Raise(new TruckVerified(Id.Value, Plate, actor) { OccurredAt = ChangedAt });
        return true;
    }

    public bool Archive(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == TruckStatus.Archived)
        {
            return false;
        }

        Status = TruckStatus.Archived;
        ChangedAt = clock.GetUtcNow();
        Raise(new TruckArchived(Id.Value, Plate, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Back in use; an archived truck comes back active (it was in use before, so it was known).</summary>
    public bool Restore(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != TruckStatus.Archived)
        {
            return false;
        }

        Status = TruckStatus.Active;
        ChangedAt = clock.GetUtcNow();
        Raise(new TruckRestored(Id.Value, Plate, actor) { OccurredAt = ChangedAt });
        return true;
    }

    private void Apply(TruckDetails details)
    {
        HaulerId = details.HaulerId;
        TareKg = details.Tare?.Kg;
        TareOn = details.Tare?.On;
        TareMethod = details.Tare?.Method;
        MaxPayloadKg = details.MaxPayloadKg;
        Axles = details.Axles;
    }
}

/// <summary>Truck and hauler input rules (US-P3-SAL-04).</summary>
public static partial class TruckRules
{
    public const int PlateMaxLength = 20;
    public const int NameMaxLength = 200;
    public const int PhoneMaxLength = 40;

    /// <summary>The heaviest tare or payload accepted, in kg: no road truck comes near 100 t empty or loaded.</summary>
    public const decimal MaxKg = 100_000m;

    public static readonly Error PlateInvalid = Error.Validation(
        "weighbridge.truck_plate_invalid", "A plate has 2 to 20 letters, digits, spaces or dashes, with at least one letter or digit.");

    public static readonly Error HaulerNameInvalid = Error.Validation("weighbridge.hauler_name_invalid", $"A hauler's name is 1 to {NameMaxLength} characters.");

    public static readonly Error PhoneInvalid = Error.Validation("weighbridge.hauler_phone_invalid", $"A phone number is up to {PhoneMaxLength} characters.");

    public static readonly Error TareInvalid = Error.Validation(
        "weighbridge.truck_tare_invalid", "A registered tare is a positive mass in kg (whole kg) with the day it was established, not in the future, and how (weighed or declared).");

    public static readonly Error PayloadInvalid = Error.Validation("weighbridge.truck_payload_invalid", "The legal payload is a positive mass in kg (whole kg).");

    public static readonly Error AxlesInvalid = Error.Validation(
        "weighbridge.truck_axles_invalid", "The axle configuration is a number of axles (2 to 9), or units joined by + (e.g. 2+3).");

    /// <summary>The plate as written: capitals, single spaces, dashes kept. Letters of any script are kept (Arabic plates).</summary>
    public static Result<string> Plate(string? plate)
    {
        var value = Spaces().Replace(plate?.Trim().ToUpperInvariant() ?? string.Empty, " ");
        return value.Length is >= 2 and <= PlateMaxLength && PlatePattern().IsMatch(value) && Key(value).Length >= 2 ? value : PlateInvalid;
    }

    /// <summary>
    /// The search and uniqueness key: letters and digits only, upper case, accents and spaces removed
    /// ("123 tu-4567" → "123TU4567"), so the way a plate is typed never makes a second truck (TC-P3-WB-016).
    /// </summary>
    public static string Key(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    public static Result<string> HaulerName(string? name)
    {
        var value = name?.Trim();
        return value is { Length: > 0 and <= NameMaxLength } && Key(value).Length > 0 ? value : HaulerNameInvalid;
    }

    public static Result<string?> Phone(string? phone)
    {
        var value = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        return value is { Length: > PhoneMaxLength } ? PhoneInvalid : value;
    }

    public static Result<RegisteredTare?> Tare(decimal? kg, DateOnly? on, string? method, DateOnly today)
    {
        if (kg is null && on is null && string.IsNullOrWhiteSpace(method))
        {
            return (RegisteredTare?)null;
        }

        TareMethod? parsed = method?.Trim().ToLowerInvariant() switch
        {
            "weighed" => Domain.TareMethod.Weighed,
            "declared" => Domain.TareMethod.Declared,
            _ => null,
        };
        return kg is { } mass && Mass(mass) && on is { } day && day <= today && parsed is { } how ? new RegisteredTare(mass, day, how) : TareInvalid;
    }

    public static Result<decimal?> Payload(decimal? kg) => kg is null ? (decimal?)null : Mass(kg.Value) ? kg : PayloadInvalid;

    public static Result<string?> Axles(string? axles)
    {
        var value = string.IsNullOrWhiteSpace(axles) ? null : axles.Replace(" ", string.Empty, StringComparison.Ordinal);
        return value is null || AxlePattern().IsMatch(value) ? value : AxlesInvalid;
    }

    public static string MethodCode(TareMethod method) => method == Domain.TareMethod.Weighed ? "weighed" : "declared";

    private static bool Mass(decimal kg) => kg > 0m && kg <= MaxKg && decimal.Truncate(kg) == kg;

    [GeneratedRegex(@"^[\p{L}\p{Nd}][\p{L}\p{Nd} -]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlatePattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[2-9](\+[1-9]){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex AxlePattern();
}
