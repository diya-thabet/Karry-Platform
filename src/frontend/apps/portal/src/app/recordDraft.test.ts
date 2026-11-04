import { act, renderHook } from "@testing-library/react";
import { useRecordDraft } from "./recordDraft";

interface Thing { version: string; name: string; colour: string; size: number }
const toDraft = (record: Thing | null) => ({ name: record?.name ?? "", colour: record?.colour ?? "" });
const v1: Thing = { version: '"1"', name: "Nord", colour: "red", size: 1 };

function setup() {
  return renderHook(({ record }: { record: Thing | null }) => useRecordDraft(record, toDraft), { initialProps: { record: v1 } });
}

describe("a form's draft of a record others may change (H-C1, finding C-2)", () => {
  it("follows a newer version that changed nothing the form edits, and keeps what the person typed", () => {
    const { result, rerender } = setup();
    act(() => { result.current.setDraft({ name: "Sud", colour: "red" }); });

    rerender({ record: { ...v1, version: '"2"', size: 9 } });

    expect(result.current.version).toBe('"2"');
    expect(result.current.draft).toEqual({ name: "Sud", colour: "red" });
  });

  it("shows the newer record when the person has not touched the draft", () => {
    const { result, rerender } = setup();

    rerender({ record: { ...v1, version: '"2"', name: "Est" } });

    expect(result.current.version).toBe('"2"');
    expect(result.current.draft).toEqual({ name: "Est", colour: "red" });
  });

  it("keeps the person's draft and the old version when both sides changed, so the save is refused", () => {
    const { result, rerender } = setup();
    act(() => { result.current.setDraft({ name: "Sud", colour: "red" }); });

    rerender({ record: { ...v1, version: '"2"', name: "Est" } });
    rerender({ record: { ...v1, version: '"3"', name: "Est", size: 4 } });

    // A later version must not slip in behind the person's back either.
    expect(result.current.version).toBe('"1"');
    expect(result.current.draft).toEqual({ name: "Sud", colour: "red" });
  });

  it("takes the saved record as the new base after a save", () => {
    const { result, rerender } = setup();
    act(() => { result.current.setDraft({ name: "Sud", colour: "red" }); });
    const saved = { ...v1, version: '"2"', name: "Sud" };

    act(() => { result.current.accept(saved); });
    rerender({ record: saved });

    expect(result.current.version).toBe('"2"');
    expect(result.current.draft).toEqual({ name: "Sud", colour: "red" });
  });
});
