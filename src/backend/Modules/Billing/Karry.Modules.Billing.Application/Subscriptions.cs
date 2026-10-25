using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Billing.Application;

/// <summary>Subscriptions and invoices of the current tenant (tenant-owned, row-level security).</summary>
public interface ISubscriptionStore
{
    Task<Subscription?> FindAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Invoice>> InvoicesAsync(CancellationToken cancellationToken);

    Task<Invoice?> FindInvoiceAsync(InvoiceId id, CancellationToken cancellationToken);

    void Add(Subscription subscription);

    void Add(Invoice invoice);

    /// <summary>Saves; false when a unique key is taken (a second subscription, or an invoice number already used).</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>New terms for a subscription: plan by code (its newest version unless <c>PlanVersion</c> is given) and the rest in full.</summary>
public sealed record ChangeSubscription(string PlanCode, int? PlanVersion, string Period, int Sites, int ExtraAssets,
    IReadOnlyDictionary<string, int>? AddOns, IReadOnlyList<string>? ExtraFeatures, IReadOnlyDictionary<string, int>? LimitOverrides);

/// <summary>An operator action on the state: <c>activate</c>, <c>missed-payment</c>, <c>payment</c> or <c>cancel</c>.</summary>
public sealed record SubscriptionAction(string Action);

public sealed record RecordInvoice(string Number, decimal Amount, string Currency, DateOnly IssuedOn, DateOnly DueOn);

public sealed record PayInvoice(DateOnly PaidOn);

/// <summary>
/// What platform operators do with a tenant's subscription until the billing provider (US-P1-BIL-02, ADR-0023):
/// change its terms, move its state, record invoices and payments. Runs in the tenant's unit of work, so every change
/// is in that tenant's ledger. A payment on a PastDue, Restricted or Suspended subscription makes it Active again.
/// </summary>
public sealed class SubscriptionOperations(ISubscriptionStore store, IPlanStore plans, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("billing.subscription_not_found", "This company has no subscription.");

    public static readonly Error InvoiceNotFound = Error.NotFound("billing.invoice_not_found", "No such invoice for this company.");

    public Task<Subscription?> CurrentAsync(CancellationToken cancellationToken) => store.FindAsync(cancellationToken);

    public Task<IReadOnlyList<Invoice>> InvoicesAsync(CancellationToken cancellationToken) => store.InvoicesAsync(cancellationToken);

    public async Task<Result<Subscription>> ChangeAsync(ChangeSubscription command, string? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await store.FindAsync(cancellationToken).ConfigureAwait(false) is not { } subscription)
        {
            return NotFound;
        }

        var versions = await plans.VersionsAsync(command.PlanCode, cancellationToken).ConfigureAwait(false);
        var plan = command.PlanVersion is { } version ? versions.FirstOrDefault(p => p.Version == version) : versions.Count > 0 ? versions[0] : null;
        if (plan is null)
        {
            return Error.Validation("billing.plan_unknown", "No plan has this code and version.");
        }

        var period = PlanPriceDto.ParsePeriod(command.Period);
        var terms = SubscriptionTerms.Of(plan, period, command.Sites, command.ExtraAssets, command.AddOns, command.ExtraFeatures, command.LimitOverrides);
        var changed = subscription.ChangeTerms(clock, terms, actor);
        return changed.IsSuccess ? await SavedAsync(subscription, cancellationToken).ConfigureAwait(false) : changed.Error;
    }

    public async Task<Result<Subscription>> ActAsync(SubscriptionAction command, string? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await store.FindAsync(cancellationToken).ConfigureAwait(false) is not { } subscription)
        {
            return NotFound;
        }

        var moved = command.Action switch
        {
            "activate" => subscription.Activate(clock, actor),
            "missed-payment" => subscription.RecordMissedPayment(clock, actor),
            "payment" => subscription.RecordPayment(clock, actor),
            "cancel" => subscription.Cancel(clock, actor),
            _ => Error.Validation("billing.action_unknown", "The action is activate, missed-payment, payment or cancel."),
        };
        return moved.IsSuccess ? await SavedAsync(subscription, cancellationToken).ConfigureAwait(false) : moved.Error;
    }

    public async Task<Result<Invoice>> RecordInvoiceAsync(RecordInvoice command, string? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await store.FindAsync(cancellationToken).ConfigureAwait(false) is not { } subscription)
        {
            return NotFound;
        }

        var invoice = Invoice.Record(clock, subscription, command.Number, command.Amount, command.Currency, command.IssuedOn, command.DueOn, actor);
        if (!invoice.IsSuccess)
        {
            return invoice.Error;
        }

        store.Add(invoice.Value);
        return await store.TrySaveAsync(cancellationToken).ConfigureAwait(false)
            ? invoice.Value
            : Error.Conflict("billing.invoice_number_taken", "An invoice with this number is already recorded for this company.");
    }

    /// <summary>Marks an invoice paid; a subscription waiting for money is Active again in the same transaction.</summary>
    public async Task<Result<Invoice>> PayInvoiceAsync(InvoiceId id, PayInvoice command, string? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await store.FindInvoiceAsync(id, cancellationToken).ConfigureAwait(false) is not { } invoice)
        {
            return InvoiceNotFound;
        }

        var paid = invoice.MarkPaid(clock, command.PaidOn, actor);
        if (!paid.IsSuccess)
        {
            return paid.Error;
        }

        // Only a subscription waiting for money resumes; a cancelled one stays cancelled (its old invoice is just paid).
        if (paid.Value && await store.FindAsync(cancellationToken).ConfigureAwait(false) is
            { Status: SubscriptionStatus.PastDue or SubscriptionStatus.Restricted or SubscriptionStatus.Suspended } subscription)
        {
            var resumed = subscription.RecordPayment(clock, actor);
            if (!resumed.IsSuccess)
            {
                return resumed.Error;
            }
        }

        await store.TrySaveAsync(cancellationToken).ConfigureAwait(false);
        return invoice;
    }

    private async Task<Result<Subscription>> SavedAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        await store.TrySaveAsync(cancellationToken).ConfigureAwait(false);
        return subscription;
    }
}

/// <summary>
/// Every provisioned tenant starts with a 30-day trial of the newest Professional version (US-P1-TEN-01 AC1,
/// ADR-0023). Runs in the new tenant's unit of work (Tenancy's outbox dispatcher); idempotent: a tenant that already
/// has a subscription keeps it.
/// </summary>
public sealed class TrialOnProvisioning(ISubscriptionStore store, IPlanStore plans, TimeProvider clock) : ITenantProvisionedHandler
{
    public const string TrialPlan = "professional";

    public async Task HandleAsync(TenantSummary tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (await store.FindAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        var versions = await plans.VersionsAsync(TrialPlan, cancellationToken).ConfigureAwait(false);
        var plan = versions.Count > 0 ? versions[0] : throw new InvalidOperationException($"The trial plan '{TrialPlan}' is missing from the catalogue.");
        store.Add(Subscription.StartTrial(clock, tenant.Id, plan, actor: null));

        // Lost a race with another delivery of the same message: the other one created it.
        await store.TrySaveAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record LifecycleRun(int Tenants, int Changed, int Failed);

/// <summary>
/// Applies the time rules of the subscription state machine to every tenant (trial end, unpaid days, retention before
/// deletion; business model §2.5). Hourly in the workers and idempotent: a missed run catches up on the next.
/// </summary>
public sealed partial class AdvanceSubscriptions(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AdvanceSubscriptions> logger)
{
    public async Task<LifecycleRun> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SharedKernel.Tenancy.TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var changed = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                var store = scope.ServiceProvider.GetRequiredService<ISubscriptionStore>();
                if (await store.FindAsync(cancellationToken).ConfigureAwait(false) is { } subscription && subscription.Advance(clock))
                {
                    await store.TrySaveAsync(cancellationToken).ConfigureAwait(false);
                    changed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenant.ToString());
            }
        }

        return new LifecycleRun(tenants.Count, changed, failed);
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "Subscription lifecycle failed for tenant {Tenant}; the next run retries")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);
}
