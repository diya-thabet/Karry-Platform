import "fake-indexeddb/auto";
import { applyChanges, openOfflineDb, type AssetRow, type KarryOfflineDb } from "@karry/offline";
import { browserScanner, findMachine, labelText } from "./machines";

const token = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
const other = "234567ABCDEFGHIJKLMNOPQRST";

function machine(id: string, code: string, name: string, labelCode: string | null): AssetRow {
  return { id, code, name, type: "excavator", siteId: "s1", status: "active", make: "Caterpillar", model: "336", serialNumber: null, plate: null, labelCode };
}

async function tablet(): Promise<KarryOfflineDb> {
  const db = openOfflineDb(`machines-${crypto.randomUUID()}`);
  await applyChanges(db, {
    full: true, cursor: "c1", serverTime: "2026-10-28T10:00:00Z",
    changes: {
      sites: { upserts: [], removes: [] }, shiftPatterns: { upserts: [], removes: [] }, meters: { upserts: [], removes: [] },
      materials: { upserts: [], removes: [] }, users: { upserts: [], removes: [] },
      assets: { upserts: [machine("a1", "PE-01", "Pelle", `KRY1:${token}`), machine("a2", "PE-02", "Pelle", null), machine("t1", "TO-01", "Tombereau", `KRY1:${other}`)], removes: [] },
    },
  });
  return db;
}

describe("reading a machine's QR label (ADR-0026 §4)", () => {
  it.each([
    [`KRY1:${token}`, `KRY1:${token}`],
    [`  kry1:${token.toLowerCase()} `, `KRY1:${token}`],
    [token, `KRY1:${token}`],
    [`KRY1:${token}A`, null],
    [`KRY1:${token.slice(0, 25)}1`, null],
    ["PE-01", null],
    ["https://example.com/", null],
  ])("%j is the label %j", (scanned, label) => {
    expect(labelText(scanned)).toBe(label);
  });
});

describe("finding a machine on the tablet (P1.15, offline)", () => {
  it("selects the machine of a scanned label", async () => {
    const found = await findMachine(await tablet(), `kry1:${other}`);

    expect(found).toMatchObject({ kind: "label", machine: { id: "t1", code: "TO-01" } });
  });

  it("says when a label is unknown here, instead of searching for its text", async () => {
    expect(await findMachine(await tablet(), "KRY1:QQQQQQQQQQQQQQQQQQQQQQQQQQ")).toEqual({ kind: "unknown-label" });
  });

  it("searches codes and names for anything else", async () => {
    const found = await findMachine(await tablet(), "pe");

    expect(found.kind === "search" ? found.machines.map((m) => m.code) : found).toEqual(["PE-01", "PE-02"]);
  });
});

describe("the browser's QR reader", () => {
  const navigatorWith = (media: boolean) => (media ? { mediaDevices: { getUserMedia: vi.fn() } } : {});

  it("is absent without BarcodeDetector, without a camera API or without QR support", async () => {
    const detector = Object.assign(vi.fn(), { getSupportedFormats: () => Promise.resolve(["qr_code"]) });
    const noQr = Object.assign(vi.fn(), { getSupportedFormats: () => Promise.resolve(["ean_13"]) });

    expect(await browserScanner({ navigator: navigatorWith(true) } as unknown as typeof globalThis)).toBeNull();
    expect(await browserScanner({ BarcodeDetector: detector, navigator: navigatorWith(false) } as unknown as typeof globalThis)).toBeNull();
    expect(await browserScanner({ BarcodeDetector: noQr, navigator: navigatorWith(true) } as unknown as typeof globalThis)).toBeNull();
  });

  it("opens the back camera, reports the first QR code read and releases the camera when stopped", async () => {
    vi.useFakeTimers();
    try {
      const stop = vi.fn();
      const stream = { getTracks: () => [{ stop }] };
      const getUserMedia = vi.fn(() => Promise.resolve(stream));
      const detect = vi.fn(() => Promise.resolve([{ rawValue: `KRY1:${token}` }]));
      class Detector {
        static getSupportedFormats = () => Promise.resolve(["qr_code"]);
        detect = detect;
      }
      const scanner = await browserScanner({ BarcodeDetector: Detector, navigator: { mediaDevices: { getUserMedia } } } as unknown as typeof globalThis);
      const video = { srcObject: null as unknown, readyState: 4, play: vi.fn(() => Promise.resolve()) } as unknown as HTMLVideoElement;
      const onCode = vi.fn();

      const release = await (scanner?.start(video, onCode) ?? Promise.reject(new Error("no scanner")));
      await vi.advanceTimersByTimeAsync(300);

      expect(getUserMedia).toHaveBeenCalledWith({ video: { facingMode: "environment" }, audio: false });
      expect(onCode).toHaveBeenCalledWith(`KRY1:${token}`);
      release();
      expect(stop).toHaveBeenCalled();
      expect(video.srcObject).toBeNull();
      const reads = detect.mock.calls.length;
      await vi.advanceTimersByTimeAsync(1000);
      expect(detect.mock.calls.length).toBe(reads);
    } finally {
      vi.useRealTimers();
    }
  });
});
