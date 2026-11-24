import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { useApi } from "../app/api";
import { blobUrl } from "../assets/api";

/**
 * A meter photo of an entry (ADR-0038 §2): fetched with the person's token, shown from a `blob:` URL, kept for the
 * session (a photo never changes). While it loads, or where the browser cannot show it, a grey square keeps the row's
 * height.
 */
export function EntryPhoto({ entryId, sha256, label, size = 64 }: { entryId: string; sha256: string; label: string; size?: number }) {
  const api = useApi();
  const photo = useQuery({
    queryKey: ["fieldops", "entries", entryId, "photos", sha256],
    queryFn: async () => {
      const blob = (await api.GET("/api/v1/fieldops/entries/{entryId}/photos/{sha256}", {
        params: { path: { entryId, sha256: sha256.replace(/^sha256:/, "") } },
        parseAs: "blob",
      })).data;
      return blob ? blobUrl(blob) ?? null : null;
    },
    staleTime: Infinity,
    retry: false,
  });
  const url = photo.data ?? null;
  useEffect(() => () => { if (url) URL.revokeObjectURL(url); }, [url]);

  return url
    ? <img src={url} alt={label} width={size} height={size} className="rounded-md border border-border object-cover" style={{ width: size, height: size }} />
    : <span role="img" aria-label={label} className="inline-block rounded-md border border-border bg-surface-muted" style={{ width: size, height: size }} />;
}
