using FluentValidation;
using Karry.Modules.Identity.Domain;

namespace Karry.Modules.Identity.Application;

/// <summary>Request shape checks (400 with field errors); the domain re-validates the rules themselves.</summary>
public sealed class InviteUserValidator : AbstractValidator<InviteUser>
{
    public InviteUserValidator()
    {
        RuleFor(c => c.FirstName).NotEmpty().MaximumLength(UserAccount.NameMaxLength + 20);
        RuleFor(c => c.LastName).NotEmpty().MaximumLength(UserAccount.NameMaxLength + 20);
        RuleFor(c => c.Email).MaximumLength(254);
        RuleFor(c => c.Phone).MaximumLength(20);
        RuleFor(c => c.Roles).Cascade(CascadeMode.Stop).NotEmpty().Must(r => r!.Count <= 20).WithMessage("At most 20 role assignments.");
    }
}

public sealed class CreateFieldUserValidator : AbstractValidator<CreateFieldUser>
{
    public CreateFieldUserValidator()
    {
        RuleFor(c => c.FirstName).NotEmpty().MaximumLength(UserAccount.NameMaxLength + 20);
        RuleFor(c => c.LastName).NotEmpty().MaximumLength(UserAccount.NameMaxLength + 20);
        RuleFor(c => c.EmployeeId).NotEmpty().MaximumLength(UserAccount.EmployeeIdMaxLength + 8);
        RuleFor(c => c.Pin).NotEmpty().MaximumLength(Pin.Length + 4);
        RuleFor(c => c.Roles).Cascade(CascadeMode.Stop).NotEmpty().Must(r => r!.Count <= 20).WithMessage("At most 20 role assignments.");
    }
}

public sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitation>
{
    public AcceptInvitationValidator()
    {
        RuleFor(c => c.Token).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class CreateRoleValidator : AbstractValidator<CreateRole>
{
    public CreateRoleValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(CustomRole.NameMaxLength + 20);
        RuleFor(c => c.Description).MaximumLength(CustomRole.DescriptionMaxLength + 20);
        RuleFor(c => c.BasedOn).MaximumLength(60);
        RuleFor(c => c.Permissions).Must(p => p is null || p.Count <= RoleLimits.Permissions).WithMessage($"At most {RoleLimits.Permissions} permissions.");
        RuleFor(c => c.FieldLevels).Must(f => f is null || f.Count <= RoleLimits.Fields).WithMessage($"At most {RoleLimits.Fields} field levels.");
    }
}

public sealed class ChangeRoleValidator : AbstractValidator<ChangeRole>
{
    public ChangeRoleValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(CustomRole.NameMaxLength + 20);
        RuleFor(c => c.Description).MaximumLength(CustomRole.DescriptionMaxLength + 20);
        RuleFor(c => c.Permissions).Cascade(CascadeMode.Stop).NotNull().Must(p => p!.Count <= RoleLimits.Permissions)
            .WithMessage($"At most {RoleLimits.Permissions} permissions.");
        RuleFor(c => c.FieldLevels).Must(f => f is null || f.Count <= RoleLimits.Fields).WithMessage($"At most {RoleLimits.Fields} field levels.");
    }
}

public sealed class ChangeFieldLevelsValidator : AbstractValidator<ChangeFieldLevels>
{
    public ChangeFieldLevelsValidator()
    {
        RuleFor(c => c.FieldLevels).Cascade(CascadeMode.Stop).NotNull().Must(f => f!.Count <= RoleLimits.Fields)
            .WithMessage($"At most {RoleLimits.Fields} field levels.");
    }
}

/// <summary>Request size limits of the role endpoints (well above what the catalogue declares).</summary>
public static class RoleLimits
{
    public const int Permissions = 500;
    public const int Fields = 200;
}
