import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, notice, site } from "../testing/fakeApi";
import { noticeText, portalLink } from "./api";

const escalated = notice();
const rejected = notice({ id: "0192f0c1-0000-7000-8000-00000000c002", type: "review.rejected", parameters: { reasonCode: "wrong_reading" }, link: "/history", siteId: null });
const old = notice({ id: "0192f0c1-0000-7000-8000-00000000c003", type: "asset.unsafe", parameters: {}, link: "/assets/holds", readAt: "2026-11-23T08:00:00Z" });

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
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
