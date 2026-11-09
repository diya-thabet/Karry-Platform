import { fireEvent, render, screen } from "@testing-library/react";
import { createI18n, I18nProvider } from "@karry/i18n";
import { SignaturePad, type SignatureCapture } from "./SignaturePad";
import { signatureProblem, strokesJson, type SignatureStroke } from "./signature";

const i18n = await createI18n({ language: "en" });
const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

/** A stroke of `points` points from (x0, y0) to (x1, y1), normalised to the pad. */
function line(points: number, x0: number, y0: number, x1: number, y1: number): SignatureStroke {
  return Array.from({ length: points }, (_, i) => {
    const f = points === 1 ? 0 : i / (points - 1);
    return { t: i * 8, x: x0 + (x1 - x0) * f, y: y0 + (y1 - y0) * f, p: 0.5 };
  });
}

describe("signature minimums (ADR-0033 §2, TC-P2-SIG-008)", () => {
  it("refuses a tap, a dot, one stroke, too few points and a 3 px line, and accepts a real signature", () => {
    const signature = [line(25, 0.1, 0.5, 0.45, 0.3), line(25, 0.5, 0.6, 0.9, 0.4)];
    // A 3 px line on a 600 px pad, drawn as two strokes with enough points: still too small.
    const threePixels = [line(25, 0.5, 0.5, 0.5025, 0.5), line(25, 0.5025, 0.5, 0.505, 0.5)];

    expect(signatureProblem([])).toBe("tooFewStrokes");
    expect(signatureProblem([line(1, 0.5, 0.5, 0.5, 0.5)])).toBe("tooFewStrokes");
    expect(signatureProblem([line(60, 0.1, 0.5, 0.9, 0.5), []])).toBe("tooFewStrokes");
    expect(signatureProblem([line(19, 0.1, 0.5, 0.9, 0.5), line(20, 0.1, 0.6, 0.9, 0.6)])).toBe("tooFewPoints");
    expect(signatureProblem(threePixels)).toBe("tooSmall");
    expect(signatureProblem([line(20, 0, 0.5, 0.19, 0.5), line(20, 0, 0.4, 0.19, 0.4)])).toBe("tooSmall");
    expect(signatureProblem([line(20, 0, 0.5, 0.2, 0.5), line(20, 0, 0.4, 0.2, 0.4)])).toBeNull();
    expect(signatureProblem([line(20, 0.5, 0, 0.5, 0.2), line(20, 0.6, 0, 0.6, 0.2)])).toBeNull();
    expect(signatureProblem(signature)).toBeNull();
  });

  it("writes the strokes the same way every time: fixed member order, pad size, rounded values", () => {
    const strokes: SignatureStroke[] = [[{ t: 0.4, x: 0.123456, y: 0.98765, p: 0.333 }, { t: 16.6, x: 1, y: 0, p: 1 }], []];

    expect(strokesJson(strokes, 600.4, 199.6)).toBe('{"v":1,"w":600,"h":200,"strokes":[[{"t":0,"x":0.1235,"y":0.9877,"p":0.33},{"t":17,"x":1,"y":0,"p":1}]]}');
  });
});

describe("SignaturePad", () => {
  beforeEach(() => {
    vi.spyOn(HTMLCanvasElement.prototype, "getBoundingClientRect").mockReturnValue({ left: 0, top: 0, width: 600, height: 200, right: 600, bottom: 200, x: 0, y: 0, toJSON: () => ({}) });
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue({ beginPath: vi.fn(), moveTo: vi.fn(), lineTo: vi.fn(), stroke: vi.fn() } as unknown as CanvasRenderingContext2D);
    vi.spyOn(HTMLCanvasElement.prototype, "toBlob").mockImplementation((callback) => { callback(new Blob([png], { type: "image/png" })); });
  });

  afterEach(() => { vi.restoreAllMocks(); });

  function pad(onConfirm: (signature: SignatureCapture) => void) {
    render(<I18nProvider i18n={i18n}><SignaturePad signer="Ada Diallo · Operator" summary="End of shift EX-01" onConfirm={onConfirm} /></I18nProvider>);
    return screen.getByRole("img", { name: "Signature area" });
  }

  function draw(canvas: HTMLElement, stroke: SignatureStroke, pointerId = 1) {
    const at = (i: number) => ({ pointerId, pointerType: "touch", clientX: (stroke[i]?.x ?? 0) * 600, clientY: (stroke[i]?.y ?? 0) * 200 });
    fireEvent.pointerDown(canvas, at(0));
    for (let i = 1; i < stroke.length; i += 1) fireEvent.pointerMove(canvas, at(i));
    fireEvent.pointerUp(canvas, at(stroke.length - 1));
  }

  it("shows who signs what, refuses a tap with the reason, then hands over a real signature's strokes and PNG", async () => {
    const confirmed = vi.fn();
    const canvas = pad(confirmed);
    expect(screen.getByText("Ada Diallo · Operator")).toBeInTheDocument();
    expect(screen.getByText("I confirm the values above.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Confirm" })).toBeDisabled();

    draw(canvas, line(1, 0.5, 0.5, 0.5, 0.5));
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("A tap or a single line is not a signature.");
    expect(canvas).toHaveAttribute("aria-invalid", "true");
    expect(confirmed).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    draw(canvas, line(25, 0.1, 0.5, 0.45, 0.3));
    draw(canvas, line(25, 0.5, 0.6, 0.9, 0.4));
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    await vi.waitFor(() => { expect(confirmed).toHaveBeenCalledTimes(1); });
    const signature = confirmed.mock.calls[0]?.[0] as SignatureCapture;
    expect(signature.strokes.map((stroke) => stroke.length)).toEqual([25, 25]);
    expect(signature.strokesJson).toBe(strokesJson(signature.strokes, 600, 200));
    expect(signature.png).toEqual(png);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("draws with one pointer at a time: a second finger on the pad adds nothing", () => {
    const confirmed = vi.fn();
    const canvas = pad(confirmed);

    fireEvent.pointerDown(canvas, { pointerId: 1, pointerType: "touch", clientX: 60, clientY: 100 });
    fireEvent.pointerDown(canvas, { pointerId: 2, pointerType: "touch", clientX: 500, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 2, pointerType: "touch", clientX: 550, clientY: 150 });
    fireEvent.pointerUp(canvas, { pointerId: 1, pointerType: "touch", clientX: 60, clientY: 100 });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(screen.getByRole("alert")).toHaveTextContent("A tap or a single line is not a signature.");
  });
});
