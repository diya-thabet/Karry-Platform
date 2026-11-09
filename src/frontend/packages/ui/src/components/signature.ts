/**
 * A point of a handwritten signature (ADR-0033 §2): `x` and `y` normalised to the pad (0 to 1, from its top-left
 * corner, also in right-to-left languages: a signature is not mirrored), `t` in milliseconds since the first point,
 * `p` the pressure (0 to 1; 0.5 when the device does not report one).
 */
export interface SignaturePoint {
  t: number;
  x: number;
  y: number;
  p: number;
}

export type SignatureStroke = readonly SignaturePoint[];

/** Why a drawing is not a signature, or null when it is one. */
export type SignatureProblem = "tooFewStrokes" | "tooFewPoints" | "tooSmall";

/** Minimums of ADR-0033 §2 (TC-P2-SIG-008): a tap, a dot or a short line is not a signature. */
export const signatureMinimums = { strokes: 2, points: 40, extent: 0.2 } as const;

/**
 * Checks the minimums: at least 2 strokes, 40 points in all, and a bounding box spanning at least 20 % of the pad's
 * width or of its height (a signature is usually wide and low, so the larger side counts, not the area).
 */
export function signatureProblem(strokes: readonly SignatureStroke[]): SignatureProblem | null {
  const drawn = strokes.filter((stroke) => stroke.length > 0);
  if (drawn.length < signatureMinimums.strokes) return "tooFewStrokes";
  const points = drawn.flat();
  if (points.length < signatureMinimums.points) return "tooFewPoints";
  const xs = points.map((point) => point.x);
  const ys = points.map((point) => point.y);
  const extent = Math.max(Math.max(...xs) - Math.min(...xs), Math.max(...ys) - Math.min(...ys));
  return extent < signatureMinimums.extent ? "tooSmall" : null;
}

const round = (value: number, digits: number) => Number(value.toFixed(digits));

/**
 * The strokes as stored and hashed: `{"v":1,"w":…,"h":…,"strokes":[[{"t","x","y","p"}…]…]}`, with the pad's size in
 * CSS pixels (to redraw at the right aspect), coordinates to 4 decimals and pressure to 2. The members are always
 * written in this order, so the same strokes always give the same text and the same hash.
 */
export function strokesJson(strokes: readonly SignatureStroke[], width: number, height: number): string {
  return JSON.stringify({
    v: 1,
    w: Math.round(width),
    h: Math.round(height),
    strokes: strokes
      .filter((stroke) => stroke.length > 0)
      .map((stroke) => stroke.map((point) => ({ t: Math.round(point.t), x: round(point.x, 4), y: round(point.y, 4), p: round(point.p, 2) }))),
  });
}

/** Draws strokes on a 2D context of `width` × `height` pixels, line width following the pressure. */
export function drawStrokes(context: CanvasRenderingContext2D, strokes: readonly SignatureStroke[], width: number, height: number, color: string) {
  context.strokeStyle = color;
  context.lineCap = "round";
  context.lineJoin = "round";
  const scale = Math.min(width, height);
  for (const stroke of strokes) {
    for (let i = 1; i < stroke.length; i += 1) {
      const from = stroke[i - 1];
      const to = stroke[i];
      if (!from || !to) continue;
      context.lineWidth = Math.max(1, scale * 0.012 * (0.5 + to.p));
      context.beginPath();
      context.moveTo(from.x * width, from.y * height);
      context.lineTo(to.x * width, to.y * height);
      context.stroke();
    }
  }
}

/** The PNG a PDF prints (ADR-0033 §2): the strokes in black on a transparent background, at twice the pad's size. */
export async function signaturePng(strokes: readonly SignatureStroke[], width: number, height: number): Promise<Uint8Array> {
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(width * 2);
  canvas.height = Math.round(height * 2);
  const context = canvas.getContext("2d");
  if (!context) throw new Error("This browser cannot draw a signature image.");
  drawStrokes(context, strokes, canvas.width, canvas.height, "#000000");
  const blob = await new Promise<Blob | null>((resolve) => { canvas.toBlob(resolve, "image/png"); });
  if (!blob) throw new Error("This browser cannot encode a signature image.");
  return new Uint8Array(await blob.arrayBuffer());
}
