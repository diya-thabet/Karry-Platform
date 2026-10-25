import { InMemoryWebStorage, User, WebStorageStateStore } from "oidc-client-ts";
import { Auth, decodeClaims, safeReturnPath, signInSettings, type AuthEnvironment, type SignInManager } from "./auth";

function jwt(claims: Record<string, unknown>): string {
  // Real tokens are base64url of UTF-8 JSON.
  const encode = (value: unknown) =>
    btoa(String.fromCharCode(...new TextEncoder().encode(JSON.stringify(value)))).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return `${encode({ alg: "RS256" })}.${encode(claims)}.signature`;
}

function user(overrides: Partial<{ expiresIn: number; state: unknown }> = {}): User {
  return new User({
    access_token: jwt({ sub: "kc-1", tenant_id: "0192f0c1-a2b3-7c4d-8e9f-001122334455", exp: 9_999_999_999 }),
    id_token: "id-token",
    refresh_token: "refresh-token",
    token_type: "Bearer",
    scope: "openid",
    profile: { sub: "kc-1", given_name: "Amel", family_name: "Ben Salah", email: "amel@example.test", iss: "x", aud: "x", exp: 0, iat: 0 },
    expires_at: Math.floor(Date.now() / 1000) + (overrides.expiresIn ?? 300),
    userState: overrides.state,
  });
}

function fakeManager(overrides: Partial<Record<keyof SignInManager, unknown>> = {}) {
  const handlers: Record<string, ((...args: never[]) => void)[]> = {};
  const add = (name: string) => (handler: (...args: never[]) => void) => {
    (handlers[name] ??= []).push(handler);
    return () => undefined;
  };
  const manager = {
    signinRedirect: vi.fn(() => Promise.resolve(undefined)),
    signinRedirectCallback: vi.fn(() => Promise.resolve(user({ state: { returnTo: "/fuel?site=3#top" } }))),
    signinSilent: vi.fn(() => Promise.resolve(user())),
    signoutRedirect: vi.fn(() => Promise.resolve(undefined)),
    getUser: vi.fn(() => Promise.resolve(null)),
    removeUser: vi.fn(() => Promise.resolve(undefined)),
    events: { addUserLoaded: add("loaded"), addUserUnloaded: add("unloaded"), addSilentRenewError: add("renewError") },
    ...overrides,
  };
  const fire = (name: string) => {
    for (const handler of handlers[name] ?? []) {
      handler();
    }
  };
  return { manager: manager as unknown as SignInManager & typeof manager, fire };
}

function environment(path: string) {
  const url = new URL(path, "https://acme.karry.test");
  const replaceState = vi.fn();
  const env: AuthEnvironment = {
    location: { href: url.href, origin: url.origin, pathname: url.pathname, search: url.search, hash: url.hash },
    history: { replaceState },
    sessionStorage: window.sessionStorage,
  };
  return Object.assign(env, { replaceState });
}

describe("signInSettings", () => {
  it("uses the authorization code flow with tokens in memory and only the sign-in state in sessionStorage", () => {
    const settings = signInSettings({ authority: "https://auth.karry.test/realms/karry", clientId: "karry-portal" }, environment("/"));

    expect(settings.response_type).toBe("code");
    expect(settings.redirect_uri).toBe("https://acme.karry.test/auth/callback");
    expect(settings.popup_redirect_uri).toBe("https://acme.karry.test/auth/popup");
    expect(settings.post_logout_redirect_uri).toBe("https://acme.karry.test/");
    expect(settings.userStore).toBeInstanceOf(WebStorageStateStore);
    expect((settings.userStore as unknown as { _store: unknown })._store).toBeInstanceOf(InMemoryWebStorage);
    expect((settings.stateStore as unknown as { _store: unknown })._store).toBe(window.sessionStorage);
    expect(settings.automaticSilentRenew).toBe(true);
  });
});

describe("Auth", () => {
  beforeEach(() => {
    window.localStorage.clear();
    window.sessionStorage.clear();
  });

  it("sends a visitor to Keycloak with the page to come back to and the app language", async () => {
    const { manager } = fakeManager();
    const auth = new Auth(manager, environment("/fuel?site=3"), { language: () => "en" });

    void auth.start();
    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalled();
    });

    expect(manager.signinRedirect).toHaveBeenCalledWith({ state: { returnTo: "/fuel?site=3" }, ui_locales: "en" });
  });

  it("completes the sign-in on the callback path, returns to the original page and keeps no token in web storage", async () => {
    const { manager } = fakeManager();
    const env = environment("/auth/callback?code=abc&state=xyz");
    const auth = new Auth(manager, env, {});

    const signedIn = await auth.start();

    expect(signedIn).toEqual({ subject: "kc-1", name: "Amel Ben Salah", email: "amel@example.test", tenantId: "0192f0c1-a2b3-7c4d-8e9f-001122334455", platformRole: undefined });
    expect(env.replaceState).toHaveBeenCalledWith(null, "", "/fuel?site=3#top");
    expect(await auth.getAccessToken()).toContain(".");
    expect(Object.keys(window.localStorage)).toHaveLength(0);
    expect(Object.keys(window.sessionStorage)).toHaveLength(0);
  });

  it("starts over when the callback cannot be completed (stale or replayed link)", async () => {
    const { manager } = fakeManager({ signinRedirectCallback: vi.fn(() => Promise.reject(new Error("No matching state"))) });
    const auth = new Auth(manager, environment("/auth/callback?code=old&state=old"), {});

    void auth.start();
    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalled();
    });

    expect(manager.signinRedirect).toHaveBeenCalledWith({ state: { returnTo: "/auth/callback?code=old&state=old" } });
  });

  it("refreshes an expired token, and goes back to Keycloak when the refresh is refused", async () => {
    const { manager } = fakeManager({ getUser: vi.fn(() => Promise.resolve(user({ expiresIn: -10 }))) });
    const auth = new Auth(manager, environment("/"), {});

    expect(await auth.getAccessToken()).toBeTruthy();
    expect(manager.signinSilent).toHaveBeenCalledTimes(1);

    const refused = fakeManager({ getUser: vi.fn(() => Promise.resolve(user({ expiresIn: -10 }))), signinSilent: vi.fn(() => Promise.reject(new Error("invalid_grant"))) });
    const revoked = new Auth(refused.manager, environment("/reports"), {});
    expect(await revoked.getAccessToken()).toBeUndefined();
    expect(refused.manager.signinRedirect).toHaveBeenCalledWith({ state: { returnTo: "/reports" } });
  });

  it("goes back to Keycloak when background renewal fails (refresh token reuse detected, account deactivated)", () => {
    const { manager, fire } = fakeManager();
    new Auth(manager, environment("/work-orders"), {});

    fire("renewError");

    expect(manager.signinRedirect).toHaveBeenCalledWith({ state: { returnTo: "/work-orders" } });
  });

  it("signs out of Keycloak with the ID token and forgets the user", async () => {
    const { manager } = fakeManager();
    const auth = new Auth(manager, environment("/auth/callback?code=abc&state=xyz"), {});
    await auth.start();
    const changes = vi.fn();
    auth.subscribe(changes);

    await auth.signOut();

    expect(manager.removeUser).toHaveBeenCalled();
    expect(manager.signoutRedirect).toHaveBeenCalledWith({ id_token_hint: "id-token" });
    expect(auth.user).toBeNull();
    expect(changes).toHaveBeenCalled();
  });

  it("steps up in a pop-up that forces a new sign-in in the app language, and keeps the new tokens (US-P1-IAM-05 AC3)", async () => {
    const fresh = user();
    const { manager } = fakeManager({ signinPopup: vi.fn(() => Promise.resolve(fresh)) });
    const auth = new Auth(manager, environment("/auth/callback?code=abc&state=xyz"), { language: () => "fr" });
    await auth.start();

    await expect(auth.stepUp()).resolves.toBe(true);

    expect(manager.signinPopup).toHaveBeenCalledWith({ max_age: 0, prompt: "login", ui_locales: "fr" });
    await expect(auth.getAccessToken()).resolves.toBe(fresh.access_token);
  });

  it("reports a step-up that was closed or blocked", async () => {
    const { manager } = fakeManager({ signinPopup: vi.fn(() => Promise.reject(new Error("Popup closed by user"))) });
    const auth = new Auth(manager, environment("/auth/callback?code=abc&state=xyz"), {});
    await auth.start();

    await expect(auth.stepUp()).resolves.toBe(false);
    expect(auth.user).not.toBeNull();
  });

  it("inside the step-up pop-up, hands the result to the opener and never redirects", async () => {
    const { manager } = fakeManager({ signinPopupCallback: vi.fn(() => Promise.resolve(undefined)) });
    const auth = new Auth(manager, environment("/auth/popup?code=abc&state=xyz"), {});

    void auth.start();
    await vi.waitFor(() => {
      expect(manager.signinPopupCallback).toHaveBeenCalledWith("https://acme.karry.test/auth/popup?code=abc&state=xyz");
    });
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });
});

describe("safeReturnPath", () => {
  it.each([
    ["/fuel", "/fuel"],
    [undefined, "/"],
    ["https://evil.example/", "/"],
    ["//evil.example/", "/"],
    ["/auth/callback?code=1", "/"],
  ])("%s -> %s", (input, expected) => {
    expect(safeReturnPath(input)).toBe(expected);
  });
});

describe("decodeClaims", () => {
  it("reads the payload and tolerates malformed tokens", () => {
    expect(decodeClaims(jwt({ tenant_id: "t-1", name: "Émilie" }))).toEqual({ tenant_id: "t-1", name: "Émilie" });
    expect(decodeClaims("not-a-jwt")).toEqual({});
    expect(decodeClaims("a.!!!.c")).toEqual({});
  });
});
