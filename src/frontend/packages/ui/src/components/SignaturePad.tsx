import { useCallback, useEffect, useId, useRef, useState, type PointerEvent, type ReactNode } from "react";
import { useTranslation } from "@karry/i18n";
import { cn } from "../cn";
import { Button } from "./Button";
import { drawStrokes, signaturePng, signatureProblem, strokesJson, type SignatureProblem, type SignatureStroke } from "./signature";

/** A confirmed signature: the strokes, their stored text and the PNG; the caller hashes both into the envelope. */
export interface SignatureCapture {
  strokes: readonly SignatureStroke[];
  strokesJson: string;
  png: Uint8Array;
}

export interface SignaturePadProps {
  /** Who signs: name, role, date and time (US-P2-SIG-01 AC2), shown above the pad. */
  signer: ReactNode;
  /** What is signed, shown above the statement "I confirm the values above". */
  summary?: ReactNode;
  onConfirm: (signature: SignatureCapture) => void | Promise<void>;
  disabled?: boolean | undefined;
  className?: string | undefined;
}

/** Keeps the stroke when the pen leaves the pad; a pointer already gone (lifted at that moment) only loses that. */
function capture(event: PointerEvent<HTMLCanvasElement>, on: boolean) {
  try {
    if (on) event.currentTarget.setPointerCapture(event.pointerId);
    else event.currentTarget.releasePointerCapture(event.pointerId);
  } catch {
    // No active pointer with this ID: drawing goes on without capture.
  }
}

/**
 * Handwritten signature capture (US-P2-SIG-01, ADR-0033 §2): a full-width pad drawn with a finger, stylus or mouse
 * (pointer events, pressure when the device has it), Clear and Confirm. Confirm refuses a tap, a dot or a short line
 * (TC-P2-SIG-008) and says why; otherwise it hands over the strokes as JSON and a PNG. The drawing is never mirrored
 * in right-to-left languages, only the text and buttons around it are. Drawing is path-dependent input, so it has no
 * keyboard equivalent (WCAG 2.1.1 exception); the pad is labelled and its instructions are announced.
 */
export function SignaturePad({ signer, summary, onConfirm, disabled = false, className }: SignaturePadProps) {
  const { t } = useTranslation();
  const id = useId();
  const canvas = useRef<HTMLCanvasElement>(null);
  const strokes = useRef<{ t: number; x: number; y: number; p: number }[][]>([]);
  const start = useRef<number | null>(null);
  // The pointer drawing the current stroke: a second finger or a palm on the pad does not draw.
  const active = useRef<number | null>(null);
  const [drawn, setDrawn] = useState(false);
  const [problem, setProblem] = useState<SignatureProblem | null>(null);
  const [busy, setBusy] = useState(false);

  const redraw = useCallback(() => {
    const element = canvas.current;
    const context = element?.getContext("2d");
    if (!element || !context) return;
    const scale = window.devicePixelRatio || 1;
    const { width, height } = element.getBoundingClientRect();
    element.width = Math.round(width * scale);
    element.height = Math.round(height * scale);
    drawStrokes(context, strokes.current, element.width, element.height, getComputedStyle(element).color);
  }, []);

  useEffect(() => {
    redraw();
    if (typeof ResizeObserver === "undefined" || !canvas.current) return undefined;
    const observer = new ResizeObserver(redraw);
    observer.observe(canvas.current);
    return () => { observer.disconnect(); };
  }, [redraw]);

  const point = (event: PointerEvent<HTMLCanvasElement>) => {
    const box = event.currentTarget.getBoundingClientRect();
    start.current ??= event.timeStamp;
    const clamp = (value: number) => Math.min(1, Math.max(0, value));
    return {
      t: event.timeStamp - start.current,
      x: clamp((event.clientX - box.left) / (box.width || 1)),
      y: clamp((event.clientY - box.top) / (box.height || 1)),
      // A mouse reports 0.5 while a button is down, a finger without force sensing 0 or 0.5: both mean "no pressure".
      p: event.pointerType === "pen" || (event.pressure > 0 && event.pressure !== 0.5) ? event.pressure : 0.5,
    };
  };

  const down = (event: PointerEvent<HTMLCanvasElement>) => {
    if (disabled || busy || active.current !== null) return;
    event.preventDefault();
    active.current = event.pointerId;
    capture(event, true);
    strokes.current.push([point(event)]);
    setProblem(null);
    setDrawn(true);
  };

  const move = (event: PointerEvent<HTMLCanvasElement>) => {
    const stroke = strokes.current.at(-1);
    if (!stroke || event.pointerId !== active.current) return;
    stroke.push(point(event));
    const element = event.currentTarget;
    const context = element.getContext("2d");
    if (context) drawStrokes(context, [stroke.slice(-2)], element.width, element.height, getComputedStyle(element).color);
  };

  const up = (event: PointerEvent<HTMLCanvasElement>) => {
    if (event.pointerId !== active.current) return;
    active.current = null;
    capture(event, false);
  };

  const clear = () => {
    strokes.current = [];
    start.current = null;
    setDrawn(false);
    setProblem(null);
    redraw();
  };

  const confirm = async () => {
    const found = signatureProblem(strokes.current);
    setProblem(found);
    if (found || !canvas.current) return;
    const { width, height } = canvas.current.getBoundingClientRect();
    const captured = strokes.current.map((stroke) => [...stroke]);
    setBusy(true);
    try {
      await onConfirm({ strokes: captured, strokesJson: strokesJson(captured, width, height), png: await signaturePng(captured, width, height) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby={`${id}-title`} className={cn("flex flex-col gap-3", className)}>
      <h2 id={`${id}-title`} className="text-[length:var(--karry-font-control)] font-semibold text-text">{t("common.signature.title")}</h2>
      <div className="text-[length:var(--karry-font-control)] text-text">{signer}</div>
      {summary && <div className="text-[length:var(--karry-font-control)] text-text">{summary}</div>}
      <p className="font-medium text-text">{t("common.signature.statement")}</p>
      <p id={`${id}-instructions`} className="text-[length:var(--karry-font-control)] text-text-muted">{t("common.signature.instructions")}</p>
      <canvas
        ref={canvas}
        role="img"
        aria-label={t("common.signature.area")}
        aria-describedby={problem ? `${id}-instructions ${id}-problem` : `${id}-instructions`}
        aria-invalid={problem ? true : undefined}
        // `dir="ltr"`: the pad keeps its own coordinates in every language (a signature is not mirrored).
        dir="ltr"
        className={cn(
          "h-48 w-full touch-none rounded-lg border-[length:var(--karry-border-width)] border-dashed border-border bg-surface text-text",
          problem && "border-danger",
          (disabled || busy) && "cursor-not-allowed opacity-60",
        )}
        onPointerDown={down}
        onPointerMove={move}
        onPointerUp={up}
        onPointerCancel={up}
      />
      {problem && (
        <p id={`${id}-problem`} role="alert" className="text-[length:var(--karry-font-control)] font-medium text-danger">
          {t(`common.signature.problem.${problem}`)}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" onClick={clear} disabled={disabled || busy || !drawn}>{t("common.signature.clear")}</Button>
        <Button onClick={() => { void confirm(); }} disabled={disabled || !drawn} loading={busy}>{t("common.signature.confirm")}</Button>
      </div>
    </section>
  );
}
