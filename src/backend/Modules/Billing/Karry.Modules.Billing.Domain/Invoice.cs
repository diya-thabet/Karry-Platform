using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Monetary;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Billing.Domain;

public readonly record struct InvoiceId(Guid Value) : IEntityId<InvoiceId>
{
    public static InvoiceId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

[EventType("billing.invoice_recorded.v1")]
[AuditSubject("invoice", nameof(InvoiceId))]
public sealed record InvoiceRecorded(Guid InvoiceId, string Number, decimal Amount, string Currency, DateOnly IssuedOn, DateOnly DueOn, string? Actor) : DomainEvent;

[EventType("billing.invoice_paid.v1")]
[AuditSubject("invoice", nameof(InvoiceId))]
public sealed record InvoicePaid(Guid InvoiceId, string Number, DateOnly PaidOn, string? Actor) : DomainEvent;

/// <summary>
/// An invoice Karry issued outside the product, recorded by an operator until the billing provider (US-P1-BIL-02 AC3,
/// Phase 10): number, amount in one currency, issue, due and paid dates. The amount is a decimal in the currency's
/// minor units; an invoice is never edited, a correction is a credit note recorded the same way (negative amount).
/// </summary>
public sealed class Invoice : AggregateRoot<InvoiceId>, ITenantOwned
{
    public const int NumberMaxLength = 40;

    private Invoice(InvoiceId id, TenantId tenantId, SubscriptionId subscriptionId, string number, decimal amount, string currency,
        DateOnly issuedOn, DateOnly dueOn, DateTimeOffset recordedAt)
        : base(id)
    {
        TenantId = tenantId;
        SubscriptionId = subscriptionId;
        Number = number;
        Amount = amount;
        Currency = currency;
        IssuedOn = issuedOn;
        DueOn = dueOn;
        RecordedAt = recordedAt;
    }

    public TenantId TenantId { get; private set; }

    public SubscriptionId SubscriptionId { get; private set; }

    /// <summary>Karry's invoice number, unique per tenant.</summary>
    public string Number { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public DateOnly IssuedOn { get; private set; }

    public DateOnly DueOn { get; private set; }

    public DateOnly? PaidOn { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public Money Total => new(Amount, SharedKernel.Monetary.Currency.Of(Currency));

    public static Result<Invoice> Record(TimeProvider clock, Subscription subscription, string? number, decimal amount, string? currency,
        DateOnly issuedOn, DateOnly dueOn, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(subscription);
        var trimmed = number?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NumberMaxLength || trimmed.Any(char.IsControl))
        {
            return Error.Validation("billing.invoice_number_invalid", $"An invoice number is 1–{NumberMaxLength} characters.");
        }

        if (currency is null || !SharedKernel.Monetary.Currency.SupportedCodes.Contains(currency, StringComparer.Ordinal))
        {
            return Error.Validation("billing.currency_unsupported", $"Unsupported currency '{currency}'.");
        }

        var minor = SharedKernel.Monetary.Currency.Of(currency).MinorUnits;
        if (amount == 0 || decimal.Round(amount, minor) != amount)
        {
            return Error.Validation("billing.invoice_amount_invalid", $"An amount is not zero and has at most {minor} decimals in {currency}.");
        }

        if (dueOn < issuedOn)
        {
            return Error.Validation("billing.invoice_dates_invalid", "The due date is on or after the issue date.");
        }

        var now = clock.GetUtcNow();
        var invoice = new Invoice(EntityId.New<InvoiceId>(clock), subscription.TenantId, subscription.Id, trimmed, amount, currency, issuedOn, dueOn, now);
        invoice.Raise(new InvoiceRecorded(invoice.Id.Value, trimmed, amount, currency, issuedOn, dueOn, actor) { OccurredAt = now });
        return invoice;
    }

    /// <summary>Marks the invoice paid (once; repeating with the same date is a no-op).</summary>
    public Result<bool> MarkPaid(TimeProvider clock, DateOnly paidOn, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (PaidOn is { } paid)
        {
            return paid == paidOn
                ? false
                : Error.Conflict("billing.invoice_already_paid", "This invoice is already recorded as paid on another date.");
        }

        if (paidOn < IssuedOn)
        {
            return Error.Validation("billing.invoice_dates_invalid", "An invoice is paid on or after its issue date.");
        }

        PaidOn = paidOn;
        Raise(new InvoicePaid(Id.Value, Number, paidOn, actor) { OccurredAt = clock.GetUtcNow() });
        return true;
    }
}
