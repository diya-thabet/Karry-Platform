/** One call as a route sees it: the request, its URL, the identity path (without `/api/v1/identity`), the JSON body and `If-Match`. */
export interface Ctx {
  request: Request;
  url: URL;
  method: string;
  path: string;
  body: unknown;
  ifMatch: string | null;
}

/** Answers a call it knows, or null so the next route is asked. */
export type Route = (ctx: Ctx) => Response | null;

export const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

export const problem = (status: number, code: string) =>
  json(status, { type: `https://karry.app/problems/${code}`, title: code, status, code, traceId: "trace-1" });

export { nextVersion } from "./factories";

/** The first route's answer, in order. */
export function answer(routes: readonly Route[], ctx: Ctx): Response | null {
  for (const route of routes) {
    const response = route(ctx);
    if (response) {
      return response;
    }
  }

  return null;
}
