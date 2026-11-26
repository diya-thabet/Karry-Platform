import { createMemoryHistory } from "@tanstack/react-router";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, notice, site } from "../testing/fakeApi";
import { noticeText, portalLink } from "./api";
import type { BrowserPush } from "./push";

const escalated = notice();
const rejected = notice({ id: "0192f0c1-0000-7000-8000-00000000c002", type: "review.rejected", parameters: { reasonCode: "wrong_reading" }, link: "/history", siteId: null });
const old = notice({ id: "0192f0c1-0000-7000-8000-00000000c003", type: "asset.unsafe", parameters: {}, link: "/assets/holds", readAt: "2026-11-23T08:00:00Z" });

async function renderAt(path: string, api: ReturnType<typeof fakeApi>, push?: BrowserPush) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} {...(push ? { push } : {})} />);
}

/** A browser whose permission and push subscription are faked. */
function fakeBrowser(permission: NotificationPermission) {
  const state = { permission, subscribed: null as { endpoint: string; unsubscribe: () => Promise<boolean> } | null, keys: [] as string[], unsubscribed: 0 };
  const push: BrowserPush = {
    supported: true,
    permission: () => state.permission,
    requestPermission: () => { state.permission = "granted"; return Promise.resolve(state.permission); },
    subscription: () => Promise.resolve(state.subscribed),
    subscribe: (key) => {
      state.keys.push(key);
      state.subscribed = { endpoint: "https://fcm.googleapis.com/fcm/send/portal-1", unsubscribe: () => { state.unsubscribed++; state.subscribed = null; return Promise.resolve(true); } };
      return Promise.resolve(state.subscribed);
    },
  };
  return { push, state };
}

describe("the notification centre in the portal (US-P2-NOT-01 AC1, ADR-0039)", () => {
  it("counts the unread notices on the bell, lists them in words and opens one where it points, marking it read", async () => {
    const api = fakeApi({ permissions: ["verification.items.read", "organization.sites.read"], sites: [site()], notices: [escalated, rejected, old] });
    await renderAt("/", api);

    fireEvent.click(await screen.findByRole("link", { name: "Notifications, 2 new" }));
    const list = await screen.findByRole("list", { name: "Notifications" });
    expect(within(list).getAllByText("New")).toHaveLength(2);
    expect(within(list).getByText("An entry of yours was rejected: Wrong reading.")).toBeInTheDocument();
    expect(within(list).getByText("A machine was held as unsafe after its pre-start checklist.")).toBeInTheDocument();
    const row = within(list).getByText(/^An entry is waiting past its due time/).closest("li");
    if (!row) throw new Error("no row");
    fireEvent.click(within(row).getByRole("button", { name: "Open" }));

    expect(await screen.findByRole("heading", { name: "Entries to review" })).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST").map((s) => s.path)).toEqual([`/api/v1/notifications/${escalated.id}/read`]);
    await waitFor(() => { expect(screen.getByRole("link", { name: "Notifications, 1 new" })).toBeInTheDocument(); });
  });

  it("marks a notice of the tablet read without leaving, and all of them at once", async () => {
    const api = fakeApi({ permissions: [], notices: [escalated, rejected] });
    await renderAt("/notifications", api);

    const list = await screen.findByRole("list", { name: "Notifications" });
    const row = within(list).getByText("An entry of yours was rejected: Wrong reading.").closest("li");
    if (!row) throw new Error("no row");
    expect(within(row).queryByRole("button", { name: "Open" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Mark all read" }));

    await waitFor(() => { expect(screen.getByRole("link", { name: "Notifications" })).toBeInTheDocument(); });
    expect(within(list).queryAllByText("New")).toHaveLength(0);
  });

  it("lets the person turn off the daily e-mail or push for a kind of notice, never the list itself", async () => {
    const api = fakeApi({ permissions: [], noticeRulesOff: ["asset.unsafe"] });
    await renderAt("/notifications", api);

    const choices = await screen.findByRole("region", { name: "How you are told" });
    expect(within(choices).queryByRole("checkbox", { name: /In the app/ })).not.toBeInTheDocument();
    expect(within(choices).getByText("Your company does not send this kind of notice for now.")).toBeInTheDocument();
    const push = within(choices).getByRole("checkbox", { name: "Rejected entry: Push alert" });
    expect(push).toBeChecked();

    fireEvent.click(push);
    expect(await screen.findByText("Your choice is saved.")).toBeInTheDocument();
    await waitFor(() => { expect(push).not.toBeChecked(); });
    expect(api.writes().filter((w) => w.method === "PUT").map((w) => [w.path, w.body]))
      .toEqual([["/api/v1/notifications/preferences/review.rejected/push", { enabled: false }]]);
    expect(within(choices).getByRole("checkbox", { name: "Overdue review: Daily e-mail" })).toBeChecked();
  });

  it("offers alerts in this browser only while push is on for the company, and turns them on and off", async () => {
    const { push, state } = fakeBrowser("default");
    await renderAt("/notifications", fakeApi({ permissions: [] }), push);
    await screen.findByText("No notification.");
    expect(screen.queryByRole("heading", { name: "Alerts in this browser" })).not.toBeInTheDocument();
    cleanup();

    const api = fakeApi({ permissions: [], pushKey: "BKarry-key_1" });
    await renderAt("/notifications", api, push);
    fireEvent.click(await screen.findByRole("button", { name: "Turn on alerts" }));

    expect(await screen.findByText("Alerts are on in this browser.")).toBeInTheDocument();
    expect(state.keys).toEqual(["BKarry-key_1"]);
    fireEvent.click(screen.getByRole("button", { name: "Turn off alerts" }));
    expect(await screen.findByRole("button", { name: "Turn on alerts" })).toBeInTheDocument();
    expect(state.unsubscribed).toBe(1);
    expect(api.sent.filter((w) => w.path.startsWith("/api/v1/notifications/push/subscription")).map((w) => [w.method, w.path, w.body])).toEqual([
      ["PUT", "/api/v1/notifications/push/subscription", { endpoint: "https://fcm.googleapis.com/fcm/send/portal-1" }],
      ["POST", "/api/v1/notifications/push/subscription/remove", { endpoint: "https://fcm.googleapis.com/fcm/send/portal-1" }],
    ]);
  });

  it("says where to allow alerts when the browser blocks them", async () => {
    await renderAt("/notifications", fakeApi({ permissions: [], pushKey: "BKarry-key_1" }), fakeBrowser("denied").push);

    expect(await screen.findByText(/This browser blocks alerts from the portal/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Turn on alerts" })).not.toBeInTheDocument();
  });

  it("says when there is nothing", async () => {
    await renderAt("/notifications", fakeApi({ permissions: [] }));
    expect(await screen.findByText("No notification.")).toBeInTheDocument();
  });

  it("has no accessibility violation", async () => {
    await renderAt("/notifications", fakeApi({ permissions: [], notices: [escalated, rejected, old] }));
    await screen.findByRole("list", { name: "Notifications" });

    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("writes each type in words and follows only the portal's own pages", async () => {
    const i18n = await createI18n({ language: "en", pseudo: false });
    const t = i18n.t.bind(i18n);

    expect([
      noticeText(t, notice({ type: "asset.unsafe" })),
      noticeText(t, notice({ type: "integrity.fraud_suspected" })),
      noticeText(t, notice({ type: "integrity.missing_end" })),
      noticeText(t, notice({ type: "something.new" })),
    ]).toEqual([
      "A machine was held as unsafe after its pre-start checklist.",
      "A meter's continuity gap raised a fraud suspicion.",
      "A shift has no end signed.",
      "Something needs a look.",
    ]);
    expect([portalLink("/review"), portalLink("/history"), portalLink("https://elsewhere.test/")]).toEqual(["/review", null, null]);
  });
});
