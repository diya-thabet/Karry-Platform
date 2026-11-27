import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useApi } from "../app/api";
import { blobUrl } from "../assets/api";

/**
 * A meter photo of an entry (ADR-0038 §2): fetched with the person's token, shown from a `blob:` URL, kept for the
 * session (a photo never changes). While it loads, or where the browser cannot show it, a grey square keeps the row's
 * height.
 */
export function EntryPhoto({ entryId, sha256, label, size = 64 }: { entryId: string; sha256: string; label: string; size?: number }) {
  const api = useApi();
  // The bytes are what is cached and shared (a photo never changes); each photo on screen makes its own `blob:` URL and
  // revokes only that one. A shared URL was revoked when one of its users unmounted (a list re-rendering), and every
  // other place showing that photo, the review panel included, was left with a broken image.
  const photo = useQuery({
    queryKey: ["fieldops", "entries", entryId, "photos", sha256],
    queryFn: async () => (await api.GET("/api/v1/fieldops/entries/{entryId}/photos/{sha256}", {
      params: { path: { entryId, sha256: sha256.replace(/^sha256:/, "") } },
      parseAs: "blob",
    })).data ?? null,
    staleTime: Infinity,
    retry: false,
  });
  const blob = photo.data ?? null;
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    const own = blob ? blobUrl(blob) ?? null : null;
    // The URL is made where it is revoked: a memo would be revoked by StrictMode's second run and leave a dead link.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setUrl(own);
    return () => { if (own) URL.revokeObjectURL(own); };
  }, [blob]);

  return url
    ? <img src={url} alt={label} width={size} height={size} className="shrink-0 rounded-md border border-border object-cover" style={{ width: size, height: size }} />
    : <span role="img" aria-label={label} className="inline-block shrink-0 rounded-md border border-border bg-surface-muted" style={{ width: size, height: size }} />;
}
