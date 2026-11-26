import { createContext } from "react";
import { ApiError, type ApiClient } from "@karry/api-client";

/** The browser's Web Push, behind an interface so it can be tested (jsdom has none). */
export interface BrowserPush {
  supported: boolean;
  permission: () => NotificationPermission;
  requestPermission: () => Promise<NotificationPermission>;
  /** This browser's current subscription (from the portal's push worker), or null. */
  subscription: () => Promise<{ endpoint: string; unsubscribe: () => Promise<boolean> } | null>;
  /** Registers the push worker and subscribes it with the server's VAPID key (base64url). */
  subscribe: (key: string) => Promise<{ endpoint: string }>;
}

/** Alerts in this browser: "off" (push is not on for the company, or the browser has none), "blocked", "available", "on". */
export type BrowserPushStatus = "off" | "blocked" | "available" | "on";

/** The VAPID key while push is on for the company; null otherwise (404). */
async function pushKey(api: ApiClient): Promise<string | null> {
  try {
    return (await api.GET("/api/v1/notifications/push/key")).data?.publicKey ?? null;
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      return null;
    }

    throw error;
  }
}

/** Where alerts in this browser stand; a browser already subscribed is confirmed to the server (its subscription may be new). */
export async function browserPushStatus(api: ApiClient, browser: BrowserPush): Promise<BrowserPushStatus> {
  if (!browser.supported) {
    return "off";
  }

  if (browser.permission() === "denied") {
    return "blocked";
  }

  if ((await pushKey(api)) === null) {
    return "off";
  }

  const current = browser.permission() === "granted" ? await browser.subscription() : null;
  if (current) {
    await api.PUT("/api/v1/notifications/push/subscription", { body: { endpoint: current.endpoint } });
    return "on";
  }

  return "available";
}

/** Turns alerts on in this browser (from a tap: the browser asks its permission); true when on. */
export async function enableBrowserPush(api: ApiClient, browser: BrowserPush): Promise<boolean> {
  if ((await browser.requestPermission()) !== "granted") {
    return false;
  }

  const key = await pushKey(api);
  if (key === null) {
    return false;
  }

  const subscription = await browser.subscribe(key);
  await api.PUT("/api/v1/notifications/push/subscription", { body: { endpoint: subscription.endpoint } });
  return true;
}

/** Turns alerts off in this browser: unsubscribed here and forgotten by the server. */
export async function disableBrowserPush(api: ApiClient, browser: BrowserPush): Promise<void> {
  const current = await browser.subscription();
  if (!current) {
    return;
  }

  await api.POST("/api/v1/notifications/push/subscription/remove", { body: { endpoint: current.endpoint } });
  await current.unsubscribe();
}

/** The real browser: the portal's push worker (`/push-sw.js`) only handles pushes; it caches nothing. */
export function realBrowserPush(): BrowserPush {
  const supported = typeof window !== "undefined" && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
  const registration = async () => (await navigator.serviceWorker.getRegistration("/")) ?? null;
  return {
    supported,
    permission: () => (supported ? Notification.permission : "denied"),
    requestPermission: () => Notification.requestPermission(),
    subscription: async () => (await (await registration())?.pushManager.getSubscription()) ?? null,
    subscribe: async (key) => {
      await navigator.serviceWorker.register("/push-sw.js", { scope: "/" });
      const ready = await navigator.serviceWorker.ready;
      return ready.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: fromBase64Url(key) });
    },
  };
}

/** A base64url string (no padding) as bytes, as `applicationServerKey` wants them. */
export function fromBase64Url(text: string): Uint8Array<ArrayBuffer> {
  const base64 = text.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(text.length / 4) * 4, "=");
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }

  return bytes;
}

/** The browser's push for the portal's screens; null means the real browser (tests pass a fake through the App). */
export const BrowserPushContext = createContext<BrowserPush | null>(null);
