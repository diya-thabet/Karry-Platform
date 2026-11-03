import { screen } from "@testing-library/react";

/**
 * A table once its rows are there: while they load it shows skeleton rows and is busy (UX-12), so a test reading rows
 * waits for the end of the wait first.
 */
export async function findTable(name: string, options: { timeout?: number } = {}): Promise<HTMLElement> {
  const table = await screen.findByRole("table", { name }, options);
  await vi.waitFor(() => { expect(table).not.toHaveAttribute("aria-busy"); }, options);
  return table;
}
