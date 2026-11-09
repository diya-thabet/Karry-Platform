import { useEffect, useMemo, useRef, useState } from "react";
import { Camera as CameraIcon, Check, RotateCcw, X } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { Button } from "@karry/ui";
import type { Camera, CameraSession, Photo } from "./camera";

type Stage = "live" | "taking" | "preview" | "failed";

function base64(bytes: Uint8Array): string {
  let text = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }

  return btoa(text);
}

/**
 * The camera screen of a form (P2.5, ADR-0032 §5): the back camera's live picture, a tap to focus where the camera
 * can, one button to take the photo, then the photo to keep or take again. There is deliberately no file input and no
 * gallery path (TC-P2-FO-002): the only way in is the stream. Loaded in its own chunk, as the forms open it.
 */
export default function PhotoCapture({ camera, take, label, onPhoto, onCancel }: {
  /** The back camera, null where the browser has none. */
  camera: Camera | null;
  /** Takes the photo of the video's current frame (`takePhoto` with the tablet's identity). */
  take: (video: HTMLVideoElement) => Promise<Photo>;
  /** What to photograph, said above the picture (for example the meter). */
  label: string;
  onPhoto: (photo: Photo) => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation();
  const video = useRef<HTMLVideoElement>(null);
  const session = useRef<CameraSession | null>(null);
  const [stage, setStage] = useState<Stage>(camera ? "live" : "failed");
  const [photo, setPhoto] = useState<Photo | null>(null);
  const [encodeFailed, setEncodeFailed] = useState(false);

  // The stream runs only while the live picture shows: it is released for the preview, on leaving and on failure.
  const live = stage === "live" || stage === "taking";
  useEffect(() => {
    const element = video.current;
    if (!live || !camera || !element) {
      return;
    }

    let active = true;
    camera.start(element).then((started) => {
      if (active) {
        session.current = started;
      } else {
        started.stop();
      }
    }).catch(() => {
      if (active) {
        setStage("failed");
      }
    });
    return () => {
      active = false;
      session.current?.stop();
      session.current = null;
    };
  }, [live, camera]);

  // A data URL (about 270 KB at most) needs no release, which an object URL would on every path out of the preview.
  const preview = useMemo(() => (photo ? `data:${photo.contentType};base64,${base64(photo.content)}` : null), [photo]);

  const shoot = async () => {
    const element = video.current;
    if (!element || stage !== "live") {
      return;
    }

    setStage("taking");
    setEncodeFailed(false);
    try {
      setPhoto(await take(element));
      setStage("preview");
    } catch {
      setEncodeFailed(true);
      setStage("live");
    }
  };

  return (
    <section aria-label={label} className="flex flex-col gap-3">
      <h2 className="text-2xl font-bold">{label}</h2>
      {live && (
        <>
          <video ref={video} muted playsInline aria-label={t("field.camera.viewfinder")} className="aspect-[4/3] w-full rounded-xl bg-black object-cover"
            onClick={(e) => {
              const box = e.currentTarget.getBoundingClientRect();
              void session.current?.focus((e.clientX - box.left) / box.width, (e.clientY - box.top) / box.height);
            }} />
          <p className="text-text-muted">{t("field.camera.focusHelp")}</p>
          {encodeFailed && <p role="alert" className="font-medium text-danger">{t("field.camera.encodeFailed")}</p>}
          <Button block disabled={stage === "taking"} onClick={() => { void shoot(); }} className="min-h-20 text-xl">
            <CameraIcon aria-hidden className="size-8" />
            {t("field.camera.take")}
          </Button>
        </>
      )}
      {stage === "preview" && photo && (
        <>
          {preview && <img src={preview} alt={t("field.camera.preview")} className="w-full rounded-xl" />}
          <div className="grid grid-cols-2 gap-3">
            <Button variant="secondary" onClick={() => { setPhoto(null); setStage("live"); }} className="min-h-16">
              <RotateCcw aria-hidden className="size-6" />
              {t("field.camera.retake")}
            </Button>
            <Button onClick={() => { onPhoto(photo); }} className="min-h-16">
              <Check aria-hidden className="size-6" />
              {t("field.camera.use")}
            </Button>
          </div>
        </>
      )}
      {stage === "failed" && <p role="alert" className="font-medium text-danger">{t(camera ? "field.camera.failed" : "field.camera.none")}</p>}
      <Button variant="secondary" onClick={onCancel}>
        <X aria-hidden className="size-5" />
        {t("field.camera.cancel")}
      </Button>
    </section>
  );
}
