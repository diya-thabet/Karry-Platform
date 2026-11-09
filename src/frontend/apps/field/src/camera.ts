import { maxBlobBytes, sha256Hex } from "@karry/offline";

/**
 * The field app's own camera (ADR-0032 §5, P2.5): evidence comes only from the live stream of the back camera, never
 * from a file or the gallery (TC-P2-FO-002). A frame is drawn at most 1600 px on its long edge and re-encoded, which
 * drops EXIF (and the position it may carry), as WebP, else JPEG, aiming at about 200 KB; the capture record that the
 * command carries, signed with it, says which bytes the app took, when, on which tablet and at what size.
 */

export const maxPhotoEdge = 1600;
export const targetPhotoBytes = 200 * 1024;
export const thumbnailEdge = 160;

/** Encoder qualities tried in turn until a photo fits the target; the last one is kept anyway when it fits a blob. */
export const photoQualities = [0.82, 0.72, 0.62, 0.5] as const;

export type PhotoType = "image/webp" | "image/jpeg";

/** What the app writes at capture (ADR-0032 §5), named by the photo's hash; the command signs it with the record. */
export interface CaptureRecord {
  sha256: string;
  capturedAt: string;
  deviceId: string;
  appVersion: string;
  width: number;
  height: number;
}

/** A photo as the outbox takes it (`EvidenceInput`) and its capture record. */
export interface Photo {
  content: Uint8Array;
  contentType: PhotoType;
  thumbnail: Uint8Array;
  capture: CaptureRecord;
}

/** Draws `source` at `width` × `height` and encodes it; null when the browser cannot write `type`. */
export type FrameEncoder = (source: CanvasImageSource, width: number, height: number, type: PhotoType, quality: number) => Promise<Blob | null>;

/** The size of a `width` × `height` frame scaled down (never up) so its long edge is at most `edge`. */
export function fitWithin(width: number, height: number, edge: number = maxPhotoEdge): { width: number; height: number } {
  const scale = Math.min(1, edge / Math.max(width, height));
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

/** The reference to a photo in a record (`startPhoto`, `endPhoto`), as the server reads it. */
export const photoReference = (sha256: string) => `sha256:${sha256}`;

/**
 * Encodes a frame: WebP first, JPEG where the browser writes no WebP (it then answers another type), each quality in
 * turn until the photo is at most `targetPhotoBytes`; else the smallest one, as long as it fits one blob.
 */
export async function encodeFrame(source: CanvasImageSource, width: number, height: number, encode: FrameEncoder): Promise<{ content: Uint8Array; contentType: PhotoType }> {
  let smallest: { content: Uint8Array; contentType: PhotoType } | null = null;
  for (const type of ["image/webp", "image/jpeg"] as const) {
    for (const quality of photoQualities) {
      const blob = await encode(source, width, height, type, quality);
      if (blob?.type !== type) {
        break;
      }

      const content = new Uint8Array(await blob.arrayBuffer());
      if (content.byteLength > 0 && content.byteLength <= targetPhotoBytes) {
        return { content, contentType: type };
      }

      if (content.byteLength > 0 && (smallest === null || content.byteLength < smallest.content.byteLength)) {
        smallest = { content, contentType: type };
      }
    }

    if (smallest !== null) {
      break;
    }
  }

  if (smallest === null || smallest.content.byteLength > maxBlobBytes) {
    throw new Error("The photo could not be encoded within the evidence limit.");
  }

  return smallest;
}

/** Takes the photo of what the video shows now: encoded, hashed, with its thumbnail and its capture record. */
export async function takePhoto(video: HTMLVideoElement, context: { deviceId: string; appVersion: string; now?: () => Date; encode?: FrameEncoder }): Promise<Photo> {
  const encode = context.encode ?? canvasEncoder;
  const { width, height } = fitWithin(video.videoWidth, video.videoHeight);
  const capturedAt = (context.now ?? (() => new Date()))().toISOString();
  const { content, contentType } = await encodeFrame(video, width, height, encode);
  const small = fitWithin(width, height, thumbnailEdge);
  const thumbnail = await encodeFrame(video, small.width, small.height, encode);
  const capture = { sha256: await sha256Hex(content), capturedAt, deviceId: context.deviceId, appVersion: context.appVersion, width, height };
  return { content, contentType, thumbnail: thumbnail.content, capture };
}

/** The browser's encoder: a canvas of the photo's size, written with `toBlob` (which leaves out any metadata). */
export const canvasEncoder: FrameEncoder = (source, width, height, type, quality) => {
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext("2d");
  if (!context) {
    return Promise.resolve(null);
  }

  context.drawImage(source, 0, 0, width, height);
  return new Promise((resolve) => { canvas.toBlob(resolve, type, quality); });
};

/** A running camera: `focus` asks for a focus at a point of the picture (0–1 each way) where the camera can. */
export interface CameraSession {
  stop: () => void;
  focus: (x: number, y: number) => Promise<void>;
}

/** Shows the back camera in a video element. */
export interface Camera {
  start: (video: HTMLVideoElement) => Promise<CameraSession>;
}

/** The back camera through `getUserMedia`, or null where the browser has no camera API (then no photo can be taken). */
export function browserCamera(scope: typeof globalThis = globalThis): Camera | null {
  const devices = (scope.navigator as Partial<Navigator>).mediaDevices;
  if (!devices || typeof devices.getUserMedia !== "function") {
    return null;
  }

  return {
    start: async (video) => {
      const stream = await devices.getUserMedia({
        video: { facingMode: { ideal: "environment" }, width: { ideal: 1920 }, height: { ideal: 1080 } },
        audio: false,
      });
      video.srcObject = stream;
      await video.play();
      const [track] = stream.getVideoTracks();
      return {
        stop: () => {
          for (const t of stream.getTracks()) {
            t.stop();
          }

          video.srcObject = null;
        },
        // Tap to focus is not in every browser's constraints yet (Chrome on Android has it): asked for, never required.
        focus: async (x, y) => {
          // Older browsers have no getCapabilities at all.
          const capabilities = (typeof track?.getCapabilities === "function" ? track.getCapabilities() : undefined) as { focusMode?: string[] } | undefined;
          if (!track || !capabilities?.focusMode?.includes("single-shot")) {
            return;
          }

          const advanced = [{ focusMode: "single-shot", pointsOfInterest: [{ x, y }] }] as unknown as MediaTrackConstraintSet[];
          await track.applyConstraints({ advanced }).catch(() => undefined);
        },
      };
    },
  };
}
