using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Application;

/// <summary>
/// One user per default role in a demo tenant (US-P1-PLT-02 AC2), for local and demo environments only (the host
/// refuses <c>seed-demo</c> in production). Office roles get an active Keycloak account
/// <c>&lt;role&gt;@&lt;slug&gt;.karry.test</c> with the given password (privileged ones enrol TOTP at first sign-in);
/// field roles get employee ID <c>DEMO-&lt;ROLE&gt;</c> with <see cref="DemoPin"/>. Idempotent.
/// </summary>
public sealed class DemoUsersSeeder(IIdentityStore store, IIdentityProvider provider, ITenantDirectory tenants, ITenantContext context, TimeProvider clock)
{
    public const string DemoPin = "804613";

    public async Task<int> SeedAsync(TenantId tenant, string password, CancellationToken cancellationToken)
    {
        context.Set(tenant);
        var company = await tenants.FindAsync(tenant, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {tenant} is not in the catalog.");
        var created = 0;
        foreach (var role in Roles.Defaults.Values)
        {
            var roles = new[] { new RoleAssignment(role.Code, null) };
            var label = string.Concat(role.Code.Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
            var demoEmail = $"{role.Code}@{company.Slug}.karry.test";
            var demoEmployee = $"DEMO-{role.Code.ToUpperInvariant()}";
            if (await store.ExistsAsync(role.Office ? demoEmail : null, role.Office ? null : demoEmployee, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (role.Office)
            {
                var email = demoEmail;
                var (user, invitation, _) = UserAccount.Invite(clock, tenant, "Demo", label, email, null, roles, actor: "seed-demo").Value;
                var account = await provider.CreateAccountAsync(new NewAccount(tenant, email, email, user.FirstName, user.LastName, user.RequiresMfa(Roles.Find)), cancellationToken)
                    .ConfigureAwait(false);
                if (!account.IsSuccess)
                {
                    throw new InvalidOperationException($"Demo account {email}: {account.Error.Code} (an enabled Keycloak account without a Karry user).");
                }

                user.LinkAccount(account.Value);
                _ = (await store.AddAsync(user, invitation, cancellationToken).ConfigureAwait(false)).Value; // throws on a conflict
                var activated = await provider.ActivateAsync(account.Value, password, cancellationToken).ConfigureAwait(false);
                if (!activated.IsSuccess)
                {
                    throw new InvalidOperationException($"Demo password rejected by Keycloak: {activated.Error.Message}");
                }

                user.Accept(invitation, clock);
                await store.SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var user = UserAccount.CreateFieldUser(clock, tenant, "Demo", label, demoEmployee, DemoPin, roles, actor: "seed-demo").Value;
                _ = (await store.AddAsync(user, null, cancellationToken).ConfigureAwait(false)).Value;
            }

            created++;
        }

        return created;
    }
}
