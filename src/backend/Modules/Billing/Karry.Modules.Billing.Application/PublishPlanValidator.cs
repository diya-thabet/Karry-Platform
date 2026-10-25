using FluentValidation;
using Karry.Modules.Billing.Domain;

namespace Karry.Modules.Billing.Application;

/// <summary>Request shape checks (400 with field errors); the domain re-validates the rules themselves.</summary>
public sealed class PublishPlanValidator : AbstractValidator<PublishPlan>
{
    public PublishPlanValidator()
    {
        RuleFor(c => c.Code).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Plan.NameMaxLength + 20);
        RuleFor(c => c.Features).Must(f => f is null || f.Count <= 64).WithMessage("At most 64 features.");
        RuleFor(c => c.Limits).Must(l => l is null || l.Count <= 32).WithMessage("At most 32 limits.");
        RuleFor(c => c.Prices).Must(p => p is null || p.Count <= 64).WithMessage("At most 64 prices.");
    }
}

public sealed class ChangeSubscriptionValidator : AbstractValidator<ChangeSubscription>
{
    public ChangeSubscriptionValidator()
    {
        RuleFor(c => c.PlanCode).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Period).Must(p => p is "monthly" or "annual").WithMessage("The period is monthly or annual.");
        RuleFor(c => c.Sites).InclusiveBetween(1, SubscriptionTerms.MaxSites);
        RuleFor(c => c.ExtraAssets).GreaterThanOrEqualTo(0);
        RuleFor(c => c.AddOns).Must(a => a is null || a.Count <= 16).WithMessage("At most 16 add-ons.");
        RuleFor(c => c.ExtraFeatures).Must(f => f is null || f.Count <= 64).WithMessage("At most 64 features.");
        RuleFor(c => c.LimitOverrides).Must(l => l is null || l.Count <= 32).WithMessage("At most 32 limits.");
    }
}

public sealed class RecordInvoiceValidator : AbstractValidator<RecordInvoice>
{
    public RecordInvoiceValidator()
    {
        RuleFor(c => c.Number).NotEmpty().MaximumLength(Invoice.NumberMaxLength);
        RuleFor(c => c.Currency).NotEmpty().Length(3);
    }
}
