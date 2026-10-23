/**
 * A failed API call as the UI sees it: the HTTP status, the stable problem `code` (RFC 9457, api-standards) and the
 * correlation ID the API returned (`X-Correlation-Id` = trace ID), which the error dialog shows to the user for support.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly correlationId: string | undefined;

  constructor(status: number, code: string, correlationId: string | undefined, message?: string) {
    super(message ?? `${String(status)} ${code}`);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.correlationId = correlationId;
  }

  /** Builds the error from a failed fetch Response (problem details body when present). */
  static async fromResponse(response: Response): Promise<ApiError> {
    const correlationId = response.headers.get("X-Correlation-Id") ?? undefined;
    let code = "http.error";
    let title: string | undefined;
    try {
      const problem = (await response.json()) as { code?: unknown; title?: unknown; traceId?: unknown };
      code = typeof problem.code === "string" ? problem.code : code;
      title = typeof problem.title === "string" ? problem.title : undefined;
      return new ApiError(response.status, code, correlationId ?? (typeof problem.traceId === "string" ? problem.traceId : undefined), title);
    } catch {
      return new ApiError(response.status, code, correlationId, title);
    }
  }
}
