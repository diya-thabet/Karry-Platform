import { constants, inflateSync } from "node:zlib";
import jsQR from "jsqr";

/**
 * The address in a Karry PDF's QR code, read as a phone would (demo step 5): the PDF's grey images (the QR is drawn as an
 * 8-bit grey image, Flate-compressed, ADR-0040 §3; signature images are in colour) are inflated and decoded with jsQR.
 */
export function qrAddressOf(pdf: Buffer): string {
  const text = pdf.toString("latin1");
  const images = /<<([^>]*\/Subtype\/Image[^>]*)>>\s*stream\r?\n/g;
  for (let match = images.exec(text); match; match = images.exec(text)) {
    const header = match[1] ?? "";
    const number = (name: string) => Number(new RegExp(`/${name} (\\d+)`).exec(header)?.[1] ?? NaN);
    if (!header.includes("/ColorSpace/DeviceGray") || !header.includes("/Filter/FlateDecode") || number("BitsPerComponent") !== 8) {
      continue;
    }

    const [width, height, length] = [number("Width"), number("Height"), number("Length")];
    const start = match.index + match[0].length;
    // PDFsharp ends its Flate streams without the final block marker zlib expects: inflate what is there.
    const grey = inflateSync(pdf.subarray(start, start + length), { finishFlush: constants.Z_SYNC_FLUSH });
    const rgba = new Uint8ClampedArray(width * height * 4);
    for (let i = 0; i < width * height; i++) {
      rgba.set([grey[i] ?? 255, grey[i] ?? 255, grey[i] ?? 255, 255], i * 4);
    }

    const code = jsQR(rgba, width, height);
    if (code) {
      return code.data;
    }
  }

  throw new Error("The PDF has no readable QR code.");
}
