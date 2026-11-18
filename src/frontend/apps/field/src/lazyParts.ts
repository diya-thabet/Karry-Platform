import { lazy } from "react";

// The camera and the signature pad come in their own chunks (P2.5 decision 8). They are fetched as soon as someone
// unlocks the tablet (online, usually) and again when a form opens; once loaded, a later import needs no network.
const loadCamera = () => import("./PhotoCapture");
const loadPad = () => import("./SignStep");

export const PhotoCapture = lazy(loadCamera);
export const SignStep = lazy(loadPad);

/** Loads both parts ahead of use; a failure here is retried by the form and shown there if it persists. */
export function preloadParts(): void {
  void loadCamera().catch(() => undefined);
  void loadPad().catch(() => undefined);
}
