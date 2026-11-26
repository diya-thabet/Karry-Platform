import type { DeviceRecord, KarryOfflineDb } from "@karry/offline";
import { deviceFetch } from "./sync";

export const pushKeyPath = "/api/v1/notifications/push/device/key";
export const pushSubscriptionPath = "/api/v1/notifications/push/device/subscription";

/** The browser's Web Push, behind an interface so it can be tested (jsdom has none). */
export interface PushPlatform {
  supported: boolean;
  permission: () => NotificationPermission;
  requestPermission: () => Promise<NotificationPermission>;
  /** The service worker's current subscription, or null. */
  subscription: () => Promise<{ endpoint: string } | null>;
  /** Subscribes the service worker with the server's VAPID key (base64url). */
  subscribe: (key: string) => Promise<{ endpoint: string }>;
}

/**
 * Push alerts on this tablet (ADR-0039 §5, US-P2-OFF-04 AC2): "off" when push is not on for the company or the browser
 * cannot; "offer" when the tablet may be asked; "on" when subscribed; "blocked" when the browser's permission was
 * refused (only the browser's settings can change it).
 */
export type PushStatus = "off" | "offer" | "on" | "blocked";

export interface TabletPush {
  /** Where push stands; a tablet already allowed is (re)subscribed silently, so the server always has its endpoint. */
  status: () => Promise<PushStatus>;
  /** Asks the browser's permission (from a tap) and subscribes the tablet; true when it is on. */
  enable: () => Promise<boolean>;
}

/** The tablet's push alerts: subscribed as the tablet, for its site (it shows each person only their notices). */
export function tabletPush(db: KarryOfflineDb, platform: PushPlatform, send: typeof fetch = fetch): TabletPush {
  const device = async (): Promise<DeviceRecord | null> => (await db.device.get("this")) ?? null;
  const key = async (record: DeviceRecord): Promise<string | null> => {
    try {
      const response = await deviceFetch(db, record, "GET", pushKeyPath, send);
      return response.ok ? ((await response.json()) as { publicKey: string }).publicKey : null;
    } catch {
      return null; // offline: asked again next time the home screen shows
    }
  };
  const register = async (record: DeviceRecord, publicKey: string): Promise<boolean> => {
    const subscription = (await platform.subscription()) ?? (await platform.subscribe(publicKey));
    const response = await deviceFetch(db, record, "PUT", pushSubscriptionPath, send, { endpoint: subscription.endpoint });
    return response.ok;
  };

  return {
    status: async () => {
      const record = await device();
      if (!platform.supported || !record) {
        return "off";
      }

      if (platform.permission() === "denied") {
        return "blocked";
      }

      const publicKey = await key(record);
      if (publicKey === null) {
        return "off";
      }

      if (platform.permission() === "granted") {
        return (await register(record, publicKey)) ? "on" : "offer";
      }

      return "offer";
    },
    enable: async () => {
      const record = await device();
      if (!record || (await platform.requestPermission()) !== "granted") {
        return false;
      }

      const publicKey = await key(record);
      return publicKey !== null && (await register(record, publicKey));
    },
  };
}

/** The browser's Web Push through the service worker; unsupported where the browser has none. */
export function browserPushPlatform(): PushPlatform {
  const supported = typeof window !== "undefined" && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
  return {
    supported,
    permission: () => (supported ? Notification.permission : "denied"),
    requestPermission: () => Notification.requestPermission(),
    subscription: async () => (await navigator.serviceWorker.ready).pushManager.getSubscription(),
    subscribe: async (publicKey) =>
      (await navigator.serviceWorker.ready).pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: fromBase64Url(publicKey) }),
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
