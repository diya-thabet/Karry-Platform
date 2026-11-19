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
});
