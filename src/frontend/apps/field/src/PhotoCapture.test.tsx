import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createI18n, I18nProvider } from "@karry/i18n";
import type { Camera, Photo } from "./camera";
import PhotoCapture from "./PhotoCapture";

const photo: Photo = {
  content: new Uint8Array([1, 2, 3]), contentType: "image/webp", thumbnail: new Uint8Array([1]),
  capture: { sha256: "ab".repeat(32), capturedAt: "2026-11-09T06:02:11.250Z", deviceId: "d1", appVersion: "1.4.0", width: 1600, height: 1200 },
};

/** A camera whose sessions say when they are released. */
function fakeCamera() {
  const stop = vi.fn();
  const focus = vi.fn(() => Promise.resolve());
  const camera: Camera = { start: vi.fn(() => Promise.resolve({ stop, focus })) };
  return { camera, stop, focus };
}

async function open(camera: Camera | null, take: (video: HTMLVideoElement) => Promise<Photo> = () => Promise.resolve(photo)) {
  const onPhoto = vi.fn();
  const onCancel = vi.fn();
  const view = render(
    <I18nProvider i18n={await createI18n({ language: "fr" })}>
      <PhotoCapture camera={camera} take={take} label="Photo du compteur" onPhoto={onPhoto} onCancel={onCancel} />
    </I18nProvider>,
  );
  return { ...view, onPhoto, onCancel };
}

describe("the camera screen (P2.5, ADR-0032 §5)", () => {
  it("TC-P2-FO-002: offers no way to attach a gallery image, only the camera's stream", async () => {
    const { camera } = fakeCamera();
    const { container } = await open(camera);
    await waitFor(() => { expect(camera.start).toHaveBeenCalled(); });

    expect(container.querySelector("input")).toBeNull();
    expect(container.querySelector("[type=file], [accept], [capture]")).toBeNull();
    expect(screen.getByLabelText("Caméra : cadrez ce qu’il faut photographier").tagName).toBe("VIDEO");
  });

  it("takes the photo, releases the camera for the preview and hands the kept photo over", async () => {
    const { camera, stop } = fakeCamera();
    const take = vi.fn(() => Promise.resolve(photo));
    const { onPhoto } = await open(camera, take);
    await waitFor(() => { expect(camera.start).toHaveBeenCalledTimes(1); });

    fireEvent.click(screen.getByRole("button", { name: "Prendre la photo" }));

    expect(await screen.findByRole("img", { name: "Photo prise" })).toHaveAttribute("src", "data:image/webp;base64,AQID");
    expect(take).toHaveBeenCalledWith(expect.any(HTMLVideoElement));
    expect(stop).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Garder cette photo" }));
    expect(onPhoto).toHaveBeenCalledWith(photo);
  });

  it("opens the camera again to take the photo again", async () => {
    const { camera } = fakeCamera();
    await open(camera);
    await waitFor(() => { expect(camera.start).toHaveBeenCalledTimes(1); });
    fireEvent.click(screen.getByRole("button", { name: "Prendre la photo" }));

    fireEvent.click(await screen.findByRole("button", { name: "Reprendre" }));

    await waitFor(() => { expect(camera.start).toHaveBeenCalledTimes(2); });
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
  });

  it("focuses where the picture is tapped", async () => {
    const { camera, focus } = fakeCamera();
    await open(camera);
    await waitFor(() => { expect(camera.start).toHaveBeenCalled(); });
    const video = screen.getByLabelText("Caméra : cadrez ce qu’il faut photographier");
    video.getBoundingClientRect = () => ({ left: 0, top: 0, width: 200, height: 100 }) as DOMRect;
    await waitFor(() => { fireEvent.click(video, { clientX: 50, clientY: 75 }); expect(focus).toHaveBeenCalledWith(0.25, 0.75); });
  });

  it("says when the photo could not be saved and keeps the camera open", async () => {
    const { camera } = fakeCamera();
    await open(camera, () => Promise.reject(new Error("too large")));
    await waitFor(() => { expect(camera.start).toHaveBeenCalled(); });

    fireEvent.click(screen.getByRole("button", { name: "Prendre la photo" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("La photo n’a pas pu être enregistrée. Reprenez-la.");
    expect(screen.getByRole("button", { name: "Prendre la photo" })).toBeEnabled();
  });

  it("says when the camera is refused, or when the tablet has none, and lets the operator leave", async () => {
    const refused: Camera = { start: () => Promise.reject(new DOMException("denied", "NotAllowedError")) };
    const first = await open(refused);
    expect(await screen.findByRole("alert")).toHaveTextContent("La caméra n’est pas disponible.");
    first.unmount();

    const { onCancel } = await open(null);
    expect(screen.getByRole("alert")).toHaveTextContent("Cette tablette n’a pas de caméra utilisable");
    fireEvent.click(screen.getByRole("button", { name: "Annuler" }));
    expect(onCancel).toHaveBeenCalled();
  });
});
