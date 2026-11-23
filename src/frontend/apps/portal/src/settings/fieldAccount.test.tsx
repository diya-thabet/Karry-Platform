import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, user } from "../testing/fakeApi";

const administrator = ["identity.users.manage", "identity.users.read"];
const controller = user({ id: "0192f0c1-0000-7000-8000-000000000081", firstName: "Rania", lastName: "Trabelsi", roles: [{ role: "controller", siteId: null }] });
const ownTablet = user({
  id: "0192f0c1-0000-7000-8000-000000000082", kind: "field", firstName: "Rania", lastName: "Trabelsi", email: null, employeeId: "OP-9",
  roles: [{ role: "operator", siteId: null }],
});
const gone = user({ id: "0192f0c1-0000-7000-8000-000000000083", kind: "field", firstName: "Old", lastName: "Driver", email: null, employeeId: "OP-1", status: "deactivated" });

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

/** Rania's office account (0) or field account (1): both carry her name. */
async function raniaRow(index: number) {
  const cell = (await screen.findAllByText("Rania Trabelsi", { selector: "td" })).at(index);
  if (!cell) {
    throw new Error(`No row ${String(index)} named Rania Trabelsi`);
  }

  return cell;
}

describe("a controller's field account (ADR-0037 §4, P2.8)", () => {
  it("links an office user to the same person's field account and unlinks it, offering active field accounts only", async () => {
    const api = fakeApi({ permissions: administrator, users: [controller, ownTablet, gone] });
    await renderAt("/settings/users", api);

    // The office account is listed first; the field account carries the same person's name.
    fireEvent.click(await raniaRow(0));
    fireEvent.click(await screen.findByRole("combobox", { name: "Their field account" }));
    expect(screen.queryByRole("option", { name: /Old Driver/ })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole("option", { name: "Rania Trabelsi · OP-9" }));
    fireEvent.click(screen.getByRole("button", { name: "Save the link" }));

    expect(await screen.findByText("Field account saved")).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "PUT", path: `/users/${controller.id}/field-account`, body: { fieldUserId: ownTablet.id } }]);

    fireEvent.click(screen.getByRole("combobox", { name: "Their field account" }));
    fireEvent.click(await screen.findByRole("option", { name: "None" }));
    fireEvent.click(screen.getByRole("button", { name: "Save the link" }));
    await vi.waitFor(() => {
      expect(api.writes().at(-1)).toEqual({ method: "PUT", path: `/users/${controller.id}/field-account`, body: { fieldUserId: null } });
    });
  });

  it("shows the link without changing it to someone who reads users only, and not at all on a field account", async () => {
    const api = fakeApi({ permissions: ["identity.users.read"], users: [{ ...controller, linkedFieldUserId: ownTablet.id }, ownTablet] });
    await renderAt("/settings/users", api);

    fireEvent.click(await raniaRow(0));
    expect(await screen.findByText("Rania Trabelsi · OP-9")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save the link" })).not.toBeInTheDocument();

    fireEvent.click(await raniaRow(1));
    expect(screen.queryByRole("heading", { name: "Their field account" })).not.toBeInTheDocument();
  });
});
