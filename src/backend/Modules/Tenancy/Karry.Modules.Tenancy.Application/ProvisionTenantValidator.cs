using FluentValidation;
using Karry.Modules.Tenancy.Domain;

namespace Karry.Modules.Tenancy.Application;

/// <summary>Request shape checks (400 with field errors); the domain re-validates the rules themselves.</summary>
public sealed class ProvisionTenantValidator : AbstractValidator<ProvisionTenant>
{
    public ProvisionTenantValidator()
    {
        RuleFor(c => c.Slug).NotEmpty().MaximumLength(TenantSlug.MaxLength + 10);
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Tenant.NameMaxLength + 50);
        When(c => c.Admin is not null, () =>
        {
            RuleFor(c => c.Admin!.FirstName).NotEmpty().MaximumLength(80);
            RuleFor(c => c.Admin!.LastName).NotEmpty().MaximumLength(80);
            RuleFor(c => c.Admin!.Email).NotEmpty().MaximumLength(254).EmailAddress();
        });
    }
}
