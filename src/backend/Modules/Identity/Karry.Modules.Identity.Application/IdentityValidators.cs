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
