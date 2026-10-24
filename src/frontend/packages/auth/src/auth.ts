import { InMemoryWebStorage, UserManager, WebStorageStateStore, type User, type UserManagerSettings } from "oidc-client-ts";

/** Where the apps sign in: the Keycloak realm (`deploy/keycloak`) and the app's client. */
export interface AuthOptions {
  /** Keycloak realm URL, e.g. `http://localhost:8080/realms/karry`. */
  authority: string;
  /** `karry-portal` or `karry-field`. */
  clientId: string;
  /** Origin of the app; the tenant address `https://<slug>.karry.app` in production. Default: the page's origin. */
  origin?: string;
  /** Path Keycloak redirects back to. Default `/auth/callback`. */
  callbackPath?: string;
  /** Language of the login page (`ui_locales`), read when the redirect happens. */
  language?: () => string;
}

/** The signed-in person, from the ID token (names) and the access token (tenant, platform role). */
export interface AuthUser {
  subject: string;
  name: string;
  email: string | undefined;
  tenantId: string | undefined;
  platformRole: string | undefined;
}

/** What {@link Auth} needs from oidc-client-ts (a seam for tests). */
export type SignInManager = Pick<
  UserManager,
  "signinRedirect" | "signinRedirectCallback" | "signinSilent" | "signoutRedirect" | "getUser" | "removeUser" | "events"
>;

/** Browser globals the flow touches (a seam for tests). */
export interface AuthEnvironment {
  location: Pick<Location, "href" | "origin" | "pathname" | "search" | "hash">;
  history: Pick<History, "replaceState">;
  sessionStorage: Storage;
}

const STATE_PREFIX = "karry.oidc.";

/**
 * oidc-client-ts settings for Karry (US-P1-IAM-01 AC1): Authorization Code + PKCE (S256), **tokens kept in memory
 * only** (never localStorage or sessionStorage: a script injected into the page cannot read them from storage, and they
 * die with the tab), refresh by the rotating refresh token. Only the short-lived sign-in state (PKCE verifier, return
 * path) goes to sessionStorage, because it must survive the redirect to Keycloak; it is removed on return.
 */
export function signInSettings(options: AuthOptions, environment: AuthEnvironment): UserManagerSettings {
  const origin = options.origin ?? environment.location.origin;
  return {
    authority: options.authority,
    client_id: options.clientId,
    redirect_uri: `${origin}${options.callbackPath ?? "/auth/callback"}`,
    post_logout_redirect_uri: `${origin}/`,
    response_type: "code",
    scope: "openid",
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    stateStore: new WebStorageStateStore({ store: environment.sessionStorage, prefix: STATE_PREFIX }),
    automaticSilentRenew: true,
    monitorSession: false,
    loadUserInfo: false,
    revokeTokenTypes: ["refresh_token"],
  };
}

interface ReturnState {
  returnTo: string;
}

/**
 * The app's session. {@link Auth.start} runs before the app renders: on the callback path it completes the sign-in,
 * anywhere else it sends the browser to Keycloak. A page reload therefore costs one redirect that the Keycloak session
 * cookie completes without a form (the tokens were in memory). A refresh that fails (expired session, refresh-token
 * reuse detected, deactivated account) also leads back to Keycloak.
 */
export class Auth {
  private current: User | null = null;
  private readonly listeners = new Set<() => void>();

  constructor(
    private readonly manager: SignInManager,
    private readonly environment: AuthEnvironment,
    private readonly options: Pick<AuthOptions, "callbackPath" | "language">,
  ) {
    manager.events.addUserLoaded((user) => {
      this.set(user);
    });
    manager.events.addUserUnloaded(() => {
      this.set(null);
    });
    manager.events.addSilentRenewError(() => {
      void this.redirect();
    });
  }

  get user(): AuthUser | null {
    return this.current ? toAuthUser(this.current) : null;
  }

  /** Signed in: the user. Otherwise the browser is on its way to Keycloak (the promise then never settles). */
  async start(): Promise<AuthUser> {
    const { location } = this.environment;
    if (location.pathname === (this.options.callbackPath ?? "/auth/callback")) {
      try {
        const user = await this.manager.signinRedirectCallback(location.href);
        this.set(user);
        const returnTo = (user.state as ReturnState | undefined)?.returnTo;
        this.environment.history.replaceState(null, "", safeReturnPath(returnTo));
        return toAuthUser(user);
      } catch {
        // Stale or replayed callback (bookmarked, back button): start a fresh sign-in.
      }
    }

    await this.redirect();
    return new Promise<AuthUser>(() => undefined);
  }

  /** A valid access token for the API (refreshed when expired); undefined while the browser goes back to Keycloak. */
  async getAccessToken(): Promise<string | undefined> {
    let user = this.current ?? (await this.manager.getUser());
    if (!user || user.expired) {
      try {
        user = await this.manager.signinSilent();
      } catch {
        user = null;
      }
    }

    if (!user) {
      await this.redirect();
      return undefined;
    }

    this.set(user);
    return user.access_token;
  }

  /** Ends the Keycloak session and returns to the app's start page (which signs in again). */
  async signOut(): Promise<void> {
    const idToken = this.current?.id_token;
    await this.manager.removeUser();
    this.set(null);
    await this.manager.signoutRedirect(idToken ? { id_token_hint: idToken } : {});
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private set(user: User | null) {
    this.current = user;
    for (const listener of this.listeners) {
      listener();
    }
  }

  private async redirect() {
    const { location } = this.environment;
    const state: ReturnState = { returnTo: `${location.pathname}${location.search}${location.hash}` };
    const language = this.options.language?.();
    await this.manager.signinRedirect({ state, ...(language ? { ui_locales: language } : {}) });
  }
}

/** Only same-app paths: an attacker-controlled state can never send the user to another site after sign-in. */
export function safeReturnPath(returnTo: string | undefined): string {
  return returnTo && returnTo.startsWith("/") && !returnTo.startsWith("//") && !returnTo.startsWith("/auth/callback") ? returnTo : "/";
}

function toAuthUser(user: User): AuthUser {
  const access = decodeClaims(user.access_token);
  const profile = user.profile;
  const fullName = [profile.given_name, profile.family_name].filter(Boolean).join(" ");
  const name = fullName.length > 0 ? fullName : (profile.preferred_username ?? profile.sub);
  return {
    subject: profile.sub,
    name,
    email: profile.email,
    tenantId: typeof access.tenant_id === "string" ? access.tenant_id : undefined,
    platformRole: typeof access.platform_role === "string" ? access.platform_role : undefined,
  };
}

/** Payload of a JWT, for display only (the API validates the token; the app trusts nothing it decodes here). */
export function decodeClaims(jwt: string): Record<string, unknown> {
  const payload = jwt.split(".")[1];
  if (!payload) {
    return {};
  }

  try {
    const base64 = payload.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(payload.length / 4) * 4, "=");
    const json = new TextDecoder().decode(Uint8Array.from(atob(base64), (c) => c.charCodeAt(0)));
    const claims: unknown = JSON.parse(json);
    return typeof claims === "object" && claims !== null ? (claims as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

/** The app's session against Keycloak in this browser tab. */
export function createAuth(options: AuthOptions, environment: AuthEnvironment = window): Auth {
  return new Auth(new UserManager(signInSettings(options, environment)), environment, options);
}
