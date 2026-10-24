import { User } from "oidc-client-ts";
import { Auth, type AuthUser, type SignInManager } from "./auth";

function base64url(value: unknown): string {
  return btoa(String.fromCharCode(...new TextEncoder().encode(JSON.stringify(value)))).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

/**
 * A signed-in session for component tests (no Keycloak): the user is "signed in" through the real callback path of
 * {@link Auth}. `signOuts` counts sign-out requests.
 */
export async function signedInAuth(person: Partial<AuthUser> & { givenName?: string; familyName?: string } = {}): Promise<Auth & { signOuts: number }> {
  const subject = person.subject ?? "test-user";
  const user = new User({
    access_token: `${base64url({ alg: "none" })}.${base64url({ sub: subject, tenant_id: person.tenantId ?? "0192f0c1-a2b3-7c4d-8e9f-001122334455" })}.test`,
    id_token: "test-id-token",
    token_type: "Bearer",
    scope: "openid",
    profile: { sub: subject, given_name: person.givenName ?? "Amel", family_name: person.familyName ?? "Ben Salah", email: person.email ?? "amel@example.test", iss: "test", aud: "test", exp: 0, iat: 0 },
    expires_at: Math.floor(Date.now() / 1000) + 300,
  });
  const noop = () => () => undefined;
  const counter = { signOuts: 0 };
  const manager = {
    signinRedirect: () => Promise.resolve(),
    signinRedirectCallback: () => Promise.resolve(user),
    signinSilent: () => Promise.resolve(user),
    signoutRedirect: () => {
      counter.signOuts++;
      return Promise.resolve();
    },
    getUser: () => Promise.resolve(user),
    removeUser: () => Promise.resolve(),
    events: { addUserLoaded: noop, addUserUnloaded: noop, addSilentRenewError: noop },
  } as unknown as SignInManager;
  const environment = {
    location: { href: "http://localhost/auth/callback?code=test", origin: "http://localhost", pathname: "/auth/callback", search: "?code=test", hash: "" },
    history: { replaceState: () => undefined },
    sessionStorage: window.sessionStorage,
  };
  const auth = new Auth(manager, environment, {});
  await auth.start();
  return Object.defineProperty(auth, "signOuts", { get: () => counter.signOuts }) as Auth & { signOuts: number };
}
