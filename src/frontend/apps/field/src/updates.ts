import { useSyncExternalStore } from "react";

/**
 * A new version of the app, waiting (US-P1-OFF-01 AC2): the service worker downloads it in the background and the
 * home screen offers it. It is never applied by itself, so nobody loses a form half filled in.
 */
export interface AppUpdates {
  subscribe: (listener: () => void) => () => void;
  available: () => boolean;
  apply: () => Promise<void>;
}

/** The store, and `offer` for the service worker registration to call when a new version is installed. */
export function createUpdates(): AppUpdates & { offer: (apply: () => Promise<void>) => void } {
  const listeners = new Set<() => void>();
  let pending: (() => Promise<void>) | null = null;
  return {
    subscribe: (listener) => {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    available: () => pending !== null,
    apply: () => pending?.() ?? Promise.resolve(),
    offer: (apply) => {
      pending = apply;
      listeners.forEach((listener) => { listener(); });
    },
  };
}

export function useUpdateAvailable(updates: AppUpdates): boolean {
  return useSyncExternalStore(updates.subscribe, updates.available);
}

/**
 * Applies a waiting version and reloads once it controls the page. The plugin's own reload only happens when the page
 * was already controlled when it loaded: a tablet that kept the app open since its first install (enrollment) would
 * press "Update" and see nothing happen (found by the P2.5 browser E2E, TC-P2-SYNC-007).
 *
 * The new version only takes over once the old one has finished the requests it is serving, so a slow or hanging
 * request can hold the switch back indefinitely. After `graceMs` the page reloads anyway: the new version is told to
 * skip waiting and takes over as soon as the old one is idle, and the outbox lives in IndexedDB, so nothing is lost.
 * The grace period starts before the request to skip waiting, which can hang or fail too.
 */
export async function applyAndReload(
  skipWaiting: () => Promise<void>,
  container: Pick<ServiceWorkerContainer, "addEventListener"> | undefined,
  reload: () => void,
  graceMs = 10_000,
): Promise<void> {
  let reloaded = false;
  const once = () => {
    if (!reloaded) {
      reloaded = true;
      reload();
    }
  };
  container?.addEventListener("controllerchange", once, { once: true });
  // The grace reload is set first: asking the new version to take over can itself hang (the old worker busy), and the
  // tablet then sat on a disabled "Update" button for good (CI browser E2E, TC-P2-SYNC-007, 2026-11-26).
  setTimeout(once, graceMs);
  try {
    await skipWaiting();
  } catch {
    // The grace reload still comes; the new version takes over at the next load.
  }
}
