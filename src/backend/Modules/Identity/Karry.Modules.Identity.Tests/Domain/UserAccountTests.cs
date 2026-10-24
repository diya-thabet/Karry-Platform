using System.Text.Json;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Identity.Tests.Domain;

[Trait("Story", "US-P1-IAM-03")]
public sealed class UserAccountTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly RoleAssignment[] Weighmaster = [new(Roles.Weighmaster, Guid.CreateVersion7())];
    private static readonly RoleAssignment[] Admin = [new(Roles.TenantAdmin, null)];

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void An_invitation_by_email_creates_an_invited_user_and_a_seven_day_single_use_token()
    {
        var (user, invitation, token) = Invite(email: "  Amel.Ben@Example.TN ", roles: Admin);

        user.Should().BeEquivalentTo(new { Status = UserStatus.Invited, Kind = UserKind.Office, Email = "amel.ben@example.tn", Phone = (string?)null, DisplayName = "Amel Ben Salah" });
        user.RequiresMfa(Roles.Find).Should().BeTrue("tenant admin is privileged");
        invitation.Channel.Should().Be(InvitationChannel.Email);
        invitation.ExpiresAt.Should().Be(clock.GetUtcNow().AddDays(7));
        invitation.TokenHash.Should().Be(Invitation.HashToken(token)).And.NotContain(token);
        Invitation.TenantOf(token).Should().Be(Tenant);
        token.Length.Should().BeGreaterThan(70, "256 bits of secret after the tenant ID");
    }

    [Fact]
    public void Ledger_events_carry_identifiers_only_never_personal_data()
    {
        var (user, _, token) = Invite(email: "amel@example.tn", roles: Admin);

        var json = JsonSerializer.Serialize(user.DomainEvents.Single(), user.DomainEvents.Single().GetType());

        json.Should().NotContainAny("amel", "Amel", "Ben Salah", token, Invitation.HashToken(token));
        json.Should().Contain(user.Id.Value.ToString()).And.Contain(Roles.TenantAdmin);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-008")]
    public void An_invitation_is_accepted_once_and_never_after_seven_days()
    {
        var (user, invitation, _) = Invite();
        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));

        user.Accept(invitation, clock).IsSuccess.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Active);
        user.DomainEvents.OfType<InvitationAccepted>().Should().ContainSingle();
        user.Accept(invitation, clock).Error!.Code.Should().Be("identity.invitation_used", "a reused link is rejected");

        var (late, lateInvitation, _) = Invite();
        clock.Advance(TimeSpan.FromDays(7));
        late.Accept(lateInvitation, clock).Error!.Code.Should().Be("identity.invitation_expired");
        late.Status.Should().Be(UserStatus.Invited);
        lateInvitation.AcceptedAt.Should().BeNull();
    }

    [Fact]
    public void A_used_invitation_is_refused_even_for_another_invited_user_record()
    {
        var (user, invitation, _) = Invite();
        user.Accept(invitation, clock);
        var (other, _, _) = Invite();

        other.Accept(invitation, clock).Error!.Code.Should().Be("identity.invitation_used");
    }

    [Theory]
    [InlineData("+21612345678", null)]
    [InlineData(null, "identity.contact_required")]
    [InlineData("0612345678", "identity.phone_invalid")]
    [InlineData("+2161234", "identity.phone_invalid")]
    public void Phone_invitations_need_an_international_number(string? phone, string? error)
    {
        var result = UserAccount.Invite(clock, Tenant, "Amel", "Ben Salah", null, phone, Admin, "admin-sub");

        if (error is null)
        {
            result.Value.Invitation.Channel.Should().Be(InvitationChannel.Phone);
            result.Value.User.Phone.Should().Be(phone);
        }
        else
        {
            result.Error!.Code.Should().Be(error);
        }
    }

    [Theory]
    [InlineData("", "Ben", "identity.name_invalid")]
    [InlineData("Amel", "   ", "identity.name_invalid")]
    [InlineData("Amel", "Ben", null)]
    public void Names_are_required(string first, string last, string? error)
    {
        var result = UserAccount.Invite(clock, Tenant, first, last, "a@example.tn", null, Admin, null);

        (result.IsSuccess ? null : result.Error.Code).Should().Be(error);
    }

    [Fact]
    public void Roles_must_be_known_and_site_scopes_must_name_a_site()
    {
        UserAccount.Invite(clock, Tenant, "A", "B", "a@example.tn", null, [], null).Error!.Code.Should().Be("identity.roles_required");
        UserAccount.Invite(clock, Tenant, "A", "B", "a@example.tn", null, [new("god", null)], null).Error!.Code.Should().Be("identity.role_unknown");
        UserAccount.Invite(clock, Tenant, "A", "B", "a@example.tn", null, [new(Roles.SiteManager, Guid.Empty)], null).Error!.Code.Should().Be("identity.site_invalid");
        UserAccount.Invite(clock, Tenant, "A", "B", "both@example.tn", "+21612345678", Admin, null).Error!.Code.Should().Be("identity.contact_required");
        UserAccount.Invite(clock, Tenant, "A", "B", "not-an-email", null, Admin, null).Error!.Code.Should().Be("identity.email_invalid");
    }

    [Theory]
    [InlineData(Roles.TenantAdmin, true)]
    [InlineData(Roles.Finance, true)]
    [InlineData(Roles.Controller, true)]
    [InlineData(Roles.Executive, true)]
    [InlineData(Roles.SiteManager, false)]
    [InlineData(Roles.Weighmaster, false)]
    [Trait("Story", "US-P1-IAM-02")]
    public void Privileged_roles_require_a_second_factor(string role, bool mfa)
    {
        Invite(roles: [new(role, null)]).User.RequiresMfa(Roles.Find).Should().Be(mfa);
    }

    [Fact]
    public void A_field_user_is_active_at_once_with_a_hashed_pin()
    {
        var user = UserAccount.CreateFieldUser(clock, Tenant, "Karim", "Trabelsi", " EMP-042 ", "804613", Weighmaster, "admin-sub").Value;

        user.Should().BeEquivalentTo(new { Kind = UserKind.Field, Status = UserStatus.Active, EmployeeId = "EMP-042", Email = (string?)null, Subject = (string?)null });
        user.PinHash.Should().StartWith("pbkdf2-sha256$600000$").And.NotContain("804613");
        Pin.Verify("804613", user.PinHash).Should().BeTrue();
        Pin.Verify("804614", user.PinHash).Should().BeFalse();
        user.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<FieldUserCreated>();
    }

    [Theory]
    [InlineData("12345", "identity.pin_invalid")]
    [InlineData("1234567", "identity.pin_invalid")]
    [InlineData("12a456", "identity.pin_invalid")]
    [InlineData("000000", "identity.pin_too_simple")]
    [InlineData("123456", "identity.pin_too_simple")]
    [InlineData("987654", "identity.pin_too_simple")]
    public void Pins_must_be_six_digits_and_not_trivial(string pin, string error)
    {
        UserAccount.CreateFieldUser(clock, Tenant, "K", "T", "EMP-1", pin, Weighmaster, null).Error!.Code.Should().Be(error);
    }

    [Fact]
    public void Field_users_cannot_hold_office_roles_and_need_a_valid_employee_id()
    {
        UserAccount.CreateFieldUser(clock, Tenant, "K", "T", "EMP-1", "804613", Admin, null).Error!.Code.Should().Be("identity.role_needs_account");
        UserAccount.CreateFieldUser(clock, Tenant, "K", "T", "EMP 1", "804613", Weighmaster, null).Error!.Code.Should().Be("identity.employee_id_invalid");
        UserAccount.CreateFieldUser(clock, Tenant, "K", "T", new string('E', 33), "804613", Weighmaster, null).Error!.Code.Should().Be("identity.employee_id_invalid");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pbkdf2-sha256$0$AA==$AA==")]
    [InlineData("md5$1$AA==$AA==")]
    [InlineData("pbkdf2-sha256$1$not-base64$AA==")]
    public void Malformed_pin_hashes_never_verify(string? stored)
    {
        Pin.Verify("804613", stored).Should().BeFalse();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-007")]
    public void Deactivation_is_final_idempotent_and_keeps_the_name()
    {
        var (user, _, _) = Invite();

        user.Deactivate(clock, "admin-sub").Should().BeTrue();
        user.Deactivate(clock, "admin-sub").Should().BeFalse();

        user.Status.Should().Be(UserStatus.Deactivated);
        user.DisplayName.Should().Be("Amel Ben Salah", "historical records keep the user's name");
        user.DeactivatedAt.Should().Be(clock.GetUtcNow());
        user.DomainEvents.OfType<UserDeactivated>().Should().ContainSingle();
        user.Accept(Invite().Invitation, clock).Error!.Code.Should().Be("identity.invitation_used");
    }

    [Fact]
    public void An_account_link_cannot_be_moved_to_another_keycloak_user()
    {
        var (user, _, _) = Invite();
        user.LinkAccount("kc-1");
        user.LinkAccount("kc-1");

        ((Action)(() => user.LinkAccount("kc-2"))).Should().Throw<InvalidOperationException>();
        user.Subject.Should().Be("kc-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("00000000000000000000000000000000.secret")]
    [InlineData("not-a-guid-not-a-guid-not-a-guid.secret")]
    [InlineData("0192f0c1a2b37c4d8e9f00112233445566")]
    public void Malformed_tokens_name_no_tenant(string? token)
    {
        Invitation.TenantOf(token).Should().BeNull();
    }

    private (UserAccount User, Invitation Invitation, string Token) Invite(string email = "amel@example.tn", RoleAssignment[]? roles = null) =>
        UserAccount.Invite(clock, Tenant, " Amel ", "Ben  Salah", email, null, roles ?? Weighmaster, "admin-sub").Value;
}
