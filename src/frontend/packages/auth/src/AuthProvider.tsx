import type { ReactNode } from "react";
import type { Auth } from "./auth";
import { AuthContext } from "./context";

/** Makes the signed-in session available to the app (render it after {@link Auth.start} resolved). */
export function AuthProvider({ auth, children }: { auth: Auth; children: ReactNode }) {
  return <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>;
}
