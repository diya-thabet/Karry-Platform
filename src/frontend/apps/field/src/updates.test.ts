import { applyAndReload } from "./updates";

describe("applying a new version (US-P1-OFF-01 AC2, TC-P2-SYNC-007)", () => {
  it("reloads once the new version controls the page, also when the page was not controlled when it loaded", async () => {
    const container = new EventTarget();
    const reload = vi.fn();
    const skipWaiting = vi.fn(() => Promise.resolve());

    await applyAndReload(skipWaiting, container, reload);
    expect(skipWaiting).toHaveBeenCalled();
    expect(reload).not.toHaveBeenCalled();

    container.dispatchEvent(new Event("controllerchange"));
    container.dispatchEvent(new Event("controllerchange"));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("reloads after the grace period when the old version is still busy and the switch does not come, and only once", async () => {
    vi.useFakeTimers();
    try {
      const container = new EventTarget();
      const reload = vi.fn();

      await applyAndReload(() => Promise.resolve(), container, reload, 10_000);
      vi.advanceTimersByTime(9_999);
      expect(reload).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(reload).toHaveBeenCalledTimes(1);

      container.dispatchEvent(new Event("controllerchange"));
      expect(reload).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("reloads after the grace period even when asking the new version to take over hangs or fails", async () => {
    vi.useFakeTimers();
    try {
      const hanging = vi.fn();
      void applyAndReload(() => new Promise<void>(() => undefined), new EventTarget(), hanging, 10_000);
      const failing = vi.fn();
      await applyAndReload(() => Promise.reject(new Error("no worker")), new EventTarget(), failing, 10_000);

      vi.advanceTimersByTime(10_000);
      expect(hanging).toHaveBeenCalledTimes(1);
      expect(failing).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });
});
