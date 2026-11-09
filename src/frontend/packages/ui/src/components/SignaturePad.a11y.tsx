import { cleanup, render, screen } from "@testing-library/react";
import { createI18n, I18nProvider } from "@karry/i18n";
import "../styles.css";
import { LanguageDirection } from "./LanguageDirection";
import { SignaturePad, type SignatureCapture } from "./SignaturePad";

// TC-P2-SIG-008 in a real Chromium: real layout, real pointer events and a real canvas (jsdom fakes all three).
afterEach(() => { cleanup(); });

function stroke(canvas: HTMLElement, from: [number, number], to: [number, number], pointerId: number) {
  const box = canvas.getBoundingClientRect();
  const at = (f: number) => ({
    pointerId, pointerType: "pen", pressure: 0.6, bubbles: true,
    clientX: box.left + box.width * (from[0] + (to[0] - from[0]) * f), clientY: box.top + box.height * (from[1] + (to[1] - from[1]) * f),
  });
  canvas.dispatchEvent(new PointerEvent("pointerdown", at(0)));
  for (let i = 1; i <= 30; i += 1) canvas.dispatchEvent(new PointerEvent("pointermove", at(i / 30)));
  canvas.dispatchEvent(new PointerEvent("pointerup", at(1)));
}

it("captures a signature in Arabic without mirroring it, and renders a PNG with the ink where it was drawn", async () => {
  const i18n = await createI18n({ language: "ar" });
  let captured: SignatureCapture | undefined;
  render(
    <I18nProvider i18n={i18n}>
      <LanguageDirection>
        <div dir="rtl" style={{ width: 600 }}>
          <SignaturePad signer="—" onConfirm={(signature) => { captured = signature; }} />
        </div>
      </LanguageDirection>
    </I18nProvider>,
  );
  const canvas = screen.getByRole("img", { name: "منطقة التوقيع" });

  // Both strokes on the left half of the pad.
  stroke(canvas, [0.05, 0.3], [0.4, 0.7], 1);
  stroke(canvas, [0.05, 0.7], [0.4, 0.3], 2);
  const confirm = screen.getByRole("button", { name: "تأكيد" });
  await vi.waitFor(() => { expect(confirm).toBeEnabled(); });
  confirm.click();
  await vi.waitFor(() => { expect(captured).toBeDefined(); });

  const signature = captured;
  if (!signature) throw new Error("not captured");
  const xs = signature.strokes.flat().map((point) => point.x);
  expect(Math.max(...xs)).toBeLessThan(0.45);
  // The pen's pressure is kept (a float32 in the event: 0.6 arrives as 0.6000000238).
  expect(new Set(signature.strokes.flat().map((point) => point.p.toFixed(2)))).toEqual(new Set(["0.60"]));
  expect(JSON.parse(signature.strokesJson)).toMatchObject({ v: 1, w: 600 });
  expect(Array.from(signature.png.slice(0, 8))).toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const image = await createImageBitmap(new Blob([signature.png as Uint8Array<ArrayBuffer>], { type: "image/png" }));
  const check = new OffscreenCanvas(image.width, image.height).getContext("2d");
  if (!check) throw new Error("no 2D context");
  check.drawImage(image, 0, 0);
  const inkIn = (x0: number, x1: number) => {
    const { data } = check.getImageData(Math.round(image.width * x0), 0, Math.round(image.width * (x1 - x0)), image.height);
    return data.some((value, i) => i % 4 === 3 && value > 0);
  };
  expect(image.width).toBe(1200);
  expect(inkIn(0, 0.5)).toBe(true);
  expect(inkIn(0.5, 1)).toBe(false);
});
