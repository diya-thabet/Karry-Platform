import { createContext, use } from "react";
import { ApiError, type ApiClient, type components } from "@karry/api-client";

export type SupportSession = components["schemas"]["SupportSessionResponse"];

/** The header that names the company and the session on every call of a support visit (ADR-0029 §4). */
export const SupportSessionHeader = "Karry-Support-Session";
export const SupportSessionInvalidCode = "support.session_invalid";

export const tenantSupportKey = (tenantId: string) => ["operator", "tenants", tenantId, "support"] as const;

/** A Karry operator looking at one company's screens in a support session. */
export interface SupportVisit {
  tenantId: string;
  tenantName: string;
  sessionId: string;
  endsAt: string;
}

/** Why a visit stopped: the operator ended it, its hour is over, or the API refused it (the company ended it). */
export type LeaveReason = "ended" | "over" | "refused";

export interface SupportVisitState {
  visit: SupportVisit | null;
  /** The company the last visit was in, so the console opens on it afterwards. */
  lastCompany: string | undefined;
  start: (visit: SupportVisit) => void;
  leave: (reason: LeaveReason) => void;
}

export const SupportVisitContext = createContext<SupportVisitState | null>(null);

// This tab only: a reload keeps the visit (the API checks the session on every call), another tab starts in the console.
export const storageKey = "karry.support-visit";

export function savedVisit(): SupportVisit | null {
  try {
    const saved = JSON.parse(sessionStorage.getItem(storageKey) ?? "null") as SupportVisit | null;
    return saved && new Date(saved.endsAt).getTime() > Date.now() ? saved : null;
  } catch {
    return null;
  }
}

export function saveVisit(visit: SupportVisit | null) {
  try {
    if (visit) {
      sessionStorage.setItem(storageKey, JSON.stringify(visit));
    } else {
      sessionStorage.removeItem(storageKey);
    }
  } catch {
    // Private browsing: the visit lasts until the page is reloaded.
  }
}

export function useSupportVisit(): SupportVisitState {
  const state = use(SupportVisitContext);
  if (!state) {
    throw new Error("useSupportVisit must be used inside <SupportVisitProvider>.");
  }

  return state;
}

type Method = "GET" | "HEAD" | "POST" | "PUT" | "PATCH" | "DELETE" | "OPTIONS" | "TRACE";
const methods: readonly Method[] = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE"];

/**
 * The same client, with the support header on every call. A refusal of the session (the company ended it, or it
 * expired) ends the visit at once instead of leaving the operator on screens that can no longer load.
 */
export function supportClient(client: ApiClient, visit: SupportVisit, onRefused: () => void): ApiClient {
  const header = { [SupportSessionHeader]: `${visit.tenantId}.${visit.sessionId}` };
  const wrapped: Partial<Record<Method, unknown>> = {};
  for (const method of methods) {
    const call = client[method] as (path: unknown, init?: { headers?: Record<string, string> }) => Promise<unknown>;
    wrapped[method] = async (path: unknown, init?: { headers?: Record<string, string> }) => {
      try {
        return await call(path, { ...init, headers: { ...init?.headers, ...header } });
      } catch (error) {
        if (error instanceof ApiError && error.code === SupportSessionInvalidCode) {
          onRefused();
        }

        throw error;
      }
    };
  }

  return { ...client, ...wrapped } as ApiClient;
}
