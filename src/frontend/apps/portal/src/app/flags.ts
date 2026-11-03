import {
  ErrorCode, StandardResolutionReasons, type JsonValue, type Provider, type ResolutionDetails,
} from "@openfeature/web-sdk";
import type { ApiClient } from "@karry/api-client";

/** The portal's OpenFeature domain (ADR-0019): its provider is set per app instance. */
export const FlagDomain = "karry-portal";

/**
 * Karry's OpenFeature web provider (ADR-0019): the signed-in person's flags, evaluated by the API (`GET /api/v1/flags`:
 * tenant, user and environment are known there). Loaded at start and on a context change; until then, and for a flag
 * the API does not know, the caller's default applies. Display only, like entitlements.
 */
export class KarryFlagProvider implements Provider {
  readonly metadata = { name: "karry-api" } as const;
  readonly runsOn = "client" as const;
  private flags: Record<string, boolean> = {};

  constructor(private readonly api: ApiClient) {}

  async initialize(): Promise<void> {
    await this.load();
  }

  async onContextChange(): Promise<void> {
    await this.load();
  }

  resolveBooleanEvaluation(flagKey: string, defaultValue: boolean): ResolutionDetails<boolean> {
    const value = this.flags[flagKey];
    return value === undefined
      ? { value: defaultValue, reason: StandardResolutionReasons.ERROR, errorCode: ErrorCode.FLAG_NOT_FOUND }
      : { value, reason: StandardResolutionReasons.STATIC };
  }

  resolveStringEvaluation(_key: string, defaultValue: string): ResolutionDetails<string> {
    return mismatch(defaultValue);
  }

  resolveNumberEvaluation(_key: string, defaultValue: number): ResolutionDetails<number> {
    return mismatch(defaultValue);
  }

  resolveObjectEvaluation<T extends JsonValue>(_key: string, defaultValue: T): ResolutionDetails<T> {
    return mismatch(defaultValue);
  }

  private async load(): Promise<void> {
    this.flags = (await this.api.GET("/api/v1/flags")).data?.flags ?? {};
  }
}

function mismatch<T>(value: T): ResolutionDetails<T> {
  return { value, reason: StandardResolutionReasons.ERROR, errorCode: ErrorCode.TYPE_MISMATCH };
}
