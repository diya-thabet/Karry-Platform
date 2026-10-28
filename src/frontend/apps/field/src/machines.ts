import { assetByLabel, searchAssets, type KarryOfflineDb, type StoredAsset } from "@karry/offline";

/** What a machine's QR label encodes (ADR-0026 §4): this prefix and a 128-bit random token in base32. */
export const labelPrefix = "KRY1:";
const tokenPattern = /^[A-Z2-7]{26}$/;

/**
 * The label text of a scan or of typed text (with or without the prefix, any case, spaces around), as the reference
 * data stores it; null when it cannot be a label. Mirrors the server's `AssetLabels.Token`.
 */
export function labelText(scanned: string): string | null {
  let text = scanned.trim().toUpperCase();
  if (text.startsWith(labelPrefix)) {
    text = text.slice(labelPrefix.length);
  }

  return tokenPattern.test(text) ? labelPrefix + text : null;
}

/** The answer to a scan or a search: the labelled machine, an unknown label, or the machines matching the words. */
export type MachineLookup =
  | { kind: "label"; machine: StoredAsset }
  | { kind: "unknown-label" }
  | { kind: "search"; machines: StoredAsset[] };

/**
 * Finds a machine on the tablet only (US-P1-OFF-04 scope "QR scan selects an asset offline"): a label selects its
 * machine; any other text searches codes, names, makes, models, serials and plates. A label the tablet does not know
 * belongs to another site or company, was reissued, or arrived after the last refresh.
 */
export async function findMachine(db: KarryOfflineDb, text: string, limit = 20): Promise<MachineLookup> {
  const label = labelText(text);
  if (label !== null) {
    const machine = await assetByLabel(db, label);
    return machine ? { kind: "label", machine } : { kind: "unknown-label" };
  }

  return { kind: "search", machines: await searchAssets(db, text, limit) };
}

/** Reads QR codes from the camera into a video element; `start` resolves to a function that stops it. */
export interface QrScanner {
  start: (video: HTMLVideoElement, onCode: (text: string) => void) => Promise<() => void>;
}

interface Detected { rawValue: string }
interface BarcodeDetectorLike { detect: (source: HTMLVideoElement) => Promise<Detected[]> }
interface BarcodeDetectorClass {
  new (options: { formats: string[] }): BarcodeDetectorLike;
  getSupportedFormats: () => Promise<string[]>;
}

/** How often a camera frame is read: enough for a held tablet, light on a low-end one. */
const frameEveryMs = 250;

/**
 * The browser's QR reader (BarcodeDetector, Chrome on Android) with the back camera, or null where the browser has
 * none or no camera: the screen then offers the typed code only.
 */
export async function browserScanner(scope: typeof globalThis = globalThis): Promise<QrScanner | null> {
  const Detector = (scope as unknown as { BarcodeDetector?: BarcodeDetectorClass }).BarcodeDetector;
  if (!Detector || !("mediaDevices" in scope.navigator) || !(await Detector.getSupportedFormats()).includes("qr_code")) {
    return null;
  }

  return {
    start: async (video, onCode) => {
      const stream = await scope.navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
      video.srcObject = stream;
      await video.play();
      const detector = new Detector({ formats: ["qr_code"] });
      let busy = false;
      const timer = setInterval(() => {
        if (busy || video.readyState < 2) {
          return;
        }

        busy = true;
        void detector.detect(video).then((codes) => {
          const first = codes[0];
          if (first) {
            onCode(first.rawValue);
          }
        }).catch(() => undefined).finally(() => { busy = false; });
      }, frameEveryMs);
      return () => {
        clearInterval(timer);
        for (const track of stream.getTracks()) {
          track.stop();
        }

        video.srcObject = null;
      };
    },
  };
}
