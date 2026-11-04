import { useState } from "react";

/** A record as the API answers it: its version is the `ETag` a save sends back as `If-Match` (H-C1). */
export interface VersionedRecord {
  version: string;
}

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/**
 * The draft of a form editing a record that other people may change meanwhile (H-C1, finding C-2).
 *
 * The form keeps the version its draft was filled from (`version`, sent as `If-Match`). When a newer record arrives
 * (a refetch on window focus, a change saved elsewhere on the same page):
 * - nothing this form edits changed (a density version, an archive): the form follows the new version, the draft stays;
 * - the person has not touched the draft: the form shows the newer record;
 * - both changed: the draft and the old version stay, so the save is refused and the conflict notice offers the choice.
 *
 * Never key the form by `changedAt`: a remount silently replaces what the person typed with someone else's change.
 */
export function useRecordDraft<R extends VersionedRecord, D>(record: R | null, toDraft: (record: R | null) => D) {
  const [base, setBase] = useState(record);
  const [draft, setDraft] = useState(() => toDraft(record));

  if (record && base && record.version !== base.version) {
    const theirs = toDraft(record);
    if (same(theirs, toDraft(base))) {
      setBase(record);
    } else if (same(draft, toDraft(base))) {
      setBase(record);
      setDraft(theirs);
    }
  }

  /** The record is now what the form shows: after the person's own save, or when they chose the other version. */
  const accept = (saved: R) => {
    setBase(saved);
    setDraft(toDraft(saved));
  };

  return { draft, setDraft, version: base?.version, accept };
}
