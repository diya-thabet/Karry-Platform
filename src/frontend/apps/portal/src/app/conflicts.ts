import { ApiError } from "@karry/api-client";

/** The API refused a save because someone else changed the record since the form was opened (H-C1, api-standards). */
export const versionConflictCode = "request.version_conflict";

export const isVersionConflict = (error: unknown) => error instanceof ApiError && error.code === versionConflictCode;

/** Mutation `meta` of a form that shows the `VersionConflict` notice itself. */
export const handlesVersionConflict = { handledCodes: [versionConflictCode] } as const;

