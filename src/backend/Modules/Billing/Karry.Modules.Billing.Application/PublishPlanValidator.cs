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
