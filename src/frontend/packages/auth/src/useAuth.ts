import { useContext, useSyncExternalStore } from "react";
import type { AuthUser } from "./auth";
import { AuthContext } from "./context";

/** The signed-in user (re-renders when it changes) and the session's actions. */
export function useAuth(): { user: AuthUser | null; signOut: () => Promise<void>; getAccessToken: () => Promise<string | undefined> } {
  const auth = useContext(AuthContext);
  if (!auth) {
    throw new Error("useAuth must be used inside <AuthProvider>.");
  }

  const user = useSyncExternalStore(
    (listener) => auth.subscribe(listener),
    () => auth.user?.subject ?? null,
  );
  return { user: user === null ? null : auth.user, signOut: () => auth.signOut(), getAccessToken: () => auth.getAccessToken() };
}
