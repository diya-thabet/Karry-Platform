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
    }
}
