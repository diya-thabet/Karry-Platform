import { maxBlobBytes, sha256Hex } from "@karry/offline";
import { browserCamera, encodeFrame, fitWithin, maxPhotoEdge, photoReference, takePhoto, targetPhotoBytes, thumbnailEdge, type FrameEncoder } from "./camera";

const frame = {} as CanvasImageSource;

/** An encoder answering `bytes(type, quality)` bytes of that type, or another type where the browser writes none. */
function encoder(bytes: (type: string, quality: number) => number, writes: string[] = ["image/webp", "image/jpeg"]) {
  return vi.fn<FrameEncoder>((_source, _width, _height, type, quality) => {
    const answered = writes.includes(type) ? type : "image/png";
    return Promise.resolve(new Blob([new Uint8Array(bytes(type, quality)).fill(7)], { type: answered }));
  });
}

describe("the photo's size (ADR-0032 §5)", () => {
  it("scales the long edge down to 1600 px, never up, keeping the shape", () => {
    expect(fitWithin(4000, 3000)).toEqual({ width: maxPhotoEdge, height: 1200 });
    expect(fitWithin(3000, 4000)).toEqual({ width: 1200, height: maxPhotoEdge });
    expect(fitWithin(1280, 720)).toEqual({ width: 1280, height: 720 });
    expect(fitWithin(1920, 1080, thumbnailEdge)).toEqual({ width: 160, height: 90 });
  });

  it("names a photo in a record as the server reads it", () => {
    expect(photoReference("ab".repeat(32))).toBe(`sha256:${"ab".repeat(32)}`);
  });
});

describe("encoding a frame", () => {
  it("keeps the first WebP quality that fits about 200 KB", async () => {
    const encode = encoder((_, q) => (q > 0.8 ? targetPhotoBytes + 1 : targetPhotoBytes));

    const photo = await encodeFrame(frame, 1600, 1200, encode);

    expect(photo.contentType).toBe("image/webp");
    expect(photo.content.byteLength).toBe(targetPhotoBytes);
    expect(encode.mock.calls.map((c) => c[4])).toEqual([0.82, 0.72]);
  });

  it("writes JPEG where the browser writes no WebP", async () => {
    const encode = encoder(() => 150_000, ["image/jpeg"]);

    const photo = await encodeFrame(frame, 1600, 1200, encode);

    expect(photo.contentType).toBe("image/jpeg");
    expect(encode.mock.calls.map((c) => c[3])).toEqual(["image/webp", "image/jpeg"]);
  });

  it("keeps the smallest photo when none reaches the target, as long as it fits one blob", async () => {
    const encode = encoder((_, q) => Math.round(500_000 * q));

    const photo = await encodeFrame(frame, 1600, 1200, encode);

    expect(photo.contentType).toBe("image/webp");
    expect(photo.content.byteLength).toBe(250_000);
  });

  it("refuses a photo that cannot fit one blob", async () => {
    await expect(encodeFrame(frame, 1600, 1200, encoder(() => maxBlobBytes + 1))).rejects.toThrow();
    await expect(encodeFrame(frame, 1600, 1200, encoder(() => 100, []))).rejects.toThrow();
  });
});

describe("taking a photo", () => {
  it("writes the capture record of the exact bytes kept: hash, device time, tablet, app version and size", async () => {
    const video = { videoWidth: 1920, videoHeight: 1080 } as HTMLVideoElement;
    const encode = encoder((_, q) => (q > 0.8 ? 250_000 : 120_000));

    const photo = await takePhoto(video, { deviceId: "d1", appVersion: "1.4.0", now: () => new Date("2026-11-09T06:02:11.250Z"), encode });

    expect(photo.contentType).toBe("image/webp");
    expect(photo.content.byteLength).toBe(120_000);
    expect(photo.capture).toEqual({
      sha256: await sha256Hex(photo.content), capturedAt: "2026-11-09T06:02:11.250Z", deviceId: "d1", appVersion: "1.4.0", width: 1600, height: 900,
    });
    expect(encode.mock.calls.at(-1)?.slice(1, 3)).toEqual([160, 90]);
    expect(photo.thumbnail.byteLength).toBeGreaterThan(0);
  });
});

describe("the browser's camera", () => {
  it("is absent without a camera API", () => {
    expect(browserCamera({ navigator: {} } as unknown as typeof globalThis)).toBeNull();
    expect(browserCamera({ navigator: { mediaDevices: {} } } as unknown as typeof globalThis)).toBeNull();
  });

  function stream(focusModes: string[] | null) {
    const stop = vi.fn();
    const applyConstraints = vi.fn(() => Promise.resolve());
    const track = { stop, applyConstraints, getCapabilities: () => (focusModes === null ? {} : { focusMode: focusModes }) };
    return { stop, applyConstraints, value: { getTracks: () => [track], getVideoTracks: () => [track] } };
  }

  it("opens the back camera without sound, focuses where tapped when it can and releases the camera when stopped", async () => {
    const media = stream(["continuous", "single-shot"]);
    const getUserMedia = vi.fn(() => Promise.resolve(media.value));
    const camera = browserCamera({ navigator: { mediaDevices: { getUserMedia } } } as unknown as typeof globalThis);
    const video = { srcObject: null as unknown, play: vi.fn(() => Promise.resolve()) } as unknown as HTMLVideoElement;

    const session = await (camera?.start(video) ?? Promise.reject(new Error("no camera")));
    await session.focus(0.25, 0.75);

    expect(getUserMedia).toHaveBeenCalledWith({ video: { facingMode: { ideal: "environment" }, width: { ideal: 1920 }, height: { ideal: 1080 } }, audio: false });
    expect(video.srcObject).toBe(media.value);
    expect(media.applyConstraints).toHaveBeenCalledWith({ advanced: [{ focusMode: "single-shot", pointsOfInterest: [{ x: 0.25, y: 0.75 }] }] });
    session.stop();
    expect(media.stop).toHaveBeenCalled();
    expect(video.srcObject).toBeNull();
  });

  it("does not ask a camera without single-shot focus to focus", async () => {
    const media = stream(null);
    const camera = browserCamera({ navigator: { mediaDevices: { getUserMedia: () => Promise.resolve(media.value) } } } as unknown as typeof globalThis);
    const video = { srcObject: null, play: () => Promise.resolve() } as unknown as HTMLVideoElement;

    const session = await (camera?.start(video) ?? Promise.reject(new Error("no camera")));
    await session.focus(0.5, 0.5);

    expect(media.applyConstraints).not.toHaveBeenCalled();
  });
});
