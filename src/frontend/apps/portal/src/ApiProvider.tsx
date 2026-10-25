import type { ReactNode } from "react";
import type { ApiClient } from "@karry/api-client";
import { ApiContext } from "./api";

export function ApiProvider({ client, children }: { client: ApiClient; children: ReactNode }) {
  return <ApiContext value={client}>{children}</ApiContext>;
}
