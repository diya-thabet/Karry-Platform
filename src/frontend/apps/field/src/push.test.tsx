import "fake-indexeddb/auto";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import { createI18n, I18nProvider } from "@karry/i18n";
import { openOfflineDb, type DeviceRecord } from "@karry/offline";
import { PushAlerts } from "./PushAlerts";
import { fromBase64Url, pushKeyPath, pushSubscriptionPath, tabletPush, type PushPlatform, type TabletPush } from "./push";

const endpoint = "https://fcm.googleapis.com/fcm/send/tablet-1";

/** An enrolled tablet, a push server answering as prepared, and a browser whose permission and subscription are faked. */
async function setup(permission: NotificationPermission, answers: Record<string, number> = {}) {
  const db = openOfflineDb(`push-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  const device: DeviceRecord = {
    id: "this", deviceId: "0192f0a1-0000-7000-8000-0000000000d1", tenantId: "0192f0a1-0000-7000-8000-0000000000a1",
    siteId: "0192f0a1-0000-7000-8000-000000000051", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32).fill(3)), enrolledAt: "2026-10-28T10:00:00Z", clockOffsetMs: 0,
  };
  await db.device.put(device);
  const calls: { method: string; url: string; body: unknown; authorization: string }[] = [];
  const send = vi.fn((url: string | URL | Request, init?: RequestInit) => {
    const path = typeof url === "string" ? url : url instanceof URL ? url.href : url.url;
    calls.push({
      method: init?.method ?? "GET", url: path, body: init?.body ? JSON.parse(init.body as string) : null,
      authorization: (init?.headers as Record<string, string | undefined>).Authorization ?? "",
    });
    const status = answers[`${init?.method ?? "GET"} ${path}`] ?? (path === pushKeyPath ? 200 : 204);
    return Promise.resolve(status === 200
      ? new Response(JSON.stringify({ publicKey: "BKarry-key_1" }), { status, headers: { "Content-Type": "application/json" } })
      : new Response(null, { status }));
  }) as unknown as typeof fetch;
  const browser = { permission, subscribed: null as { endpoint: string } | null, keys: [] as string[] };
  const platform: PushPlatform = {
    supported: true,
    permission: () => browser.permission,
    requestPermission: () => { browser.permission = "granted"; return Promise.resolve(browser.permission); },
    subscription: () => Promise.resolve(browser.subscribed),
    subscribe: (key) => { browser.keys.push(key); browser.subscribed = { endpoint }; return Promise.resolve(browser.subscribed); },
  };
  return { push: tabletPush(db, platform, send), calls, browser };
}

describe("push alerts on the tablet (ADR-0039 §5, US-P2-OFF-04 AC2)", () => {
  it("offers alerts when push is on, and a tap subscribes the tablet with the server's key as the tablet itself", async () => {
    const { push, calls, browser } = await setup("default");

    expect(await push.status()).toBe("offer");
    expect(await push.enable()).toBe(true);

    expect(browser.keys).toEqual(["BKarry-key_1"]);
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([`GET ${pushKeyPath}`, `GET ${pushKeyPath}`, `PUT ${pushSubscriptionPath}`]);
    expect(calls[2]?.body).toEqual({ endpoint });
    expect(calls.every((c) => c.authorization.startsWith("Device "))).toBe(true);
  });

  it("subscribes again silently a tablet already allowed, so the server always has its endpoint", async () => {
    const { push, calls, browser } = await setup("granted");
    browser.subscribed = { endpoint };

    expect(await push.status()).toBe("on");
    expect(browser.keys).toEqual([]);
    expect(calls.at(-1)).toMatchObject({ method: "PUT", url: pushSubscriptionPath, body: { endpoint } });
  });

  it("stays quiet when push is off for the company, the browser refused, or the browser has none", async () => {
    const off = await setup("default", { [`GET ${pushKeyPath}`]: 404 });
    const blocked = await setup("denied");
    const none = await setup("default");

    expect(await off.push.status()).toBe("off");
    expect(await blocked.push.status()).toBe("blocked");
    expect(blocked.calls).toEqual([]);
    expect(await tabletPush(openOfflineDb(`push-${crypto.randomUUID()}`), { ...none.browser, supported: false } as unknown as PushPlatform).status()).toBe("off");
  });

  it("reads the server's base64url key as bytes", () => {
    expect([...fromBase64Url("AQID_-8")]).toEqual([1, 2, 3, 255, 239]);
  });
});

async function renderAlerts(push: TabletPush | null) {
  const i18n = await createI18n({ language: "fr", pseudo: false });
  render(<I18nProvider i18n={i18n}><PushAlerts push={push} /></I18nProvider>);
}

describe("the alerts card on the home screen", () => {
  it("is shown while alerts can be turned on, and goes away once a tap turns them on", async () => {
    const enable = vi.fn(() => Promise.resolve(true));
    await renderAlerts({ status: () => Promise.resolve("offer"), enable });

    fireEvent.click(await screen.findByRole("button", { name: "Activer les alertes" }));

    await waitFor(() => { expect(screen.queryByRole("region", { name: "Alertes" })).not.toBeInTheDocument(); });
    expect(enable).toHaveBeenCalledTimes(1);
  });

  it("says so when the alerts could not be turned on, and shows nothing when there is nothing to offer", async () => {
    await renderAlerts({ status: () => Promise.resolve("offer"), enable: () => Promise.resolve(false) });
    fireEvent.click(await screen.findByRole("button", { name: "Activer les alertes" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Les alertes n’ont pas pu être activées");
  });
});
