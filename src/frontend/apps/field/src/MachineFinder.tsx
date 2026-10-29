import { useCallback, useEffect, useRef, useState } from "react";
import { ArrowLeft, ScanLine, X } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import type { StoredAsset } from "@karry/offline";
import { Button, TextField } from "@karry/ui";
import type { MachineLookup, QrScanner } from "./machines";

function MachineCard({ machine }: { machine: StoredAsset }) {
  const { t } = useTranslation();
  const details = [machine.make, machine.model, machine.plate].filter(Boolean).join(" · ");
  return (
    <section aria-label={t("field.machines.selected")} className="flex flex-col gap-1 rounded-xl border-[length:var(--karry-border-width)] border-primary bg-surface p-4">
      <p className="font-mono text-2xl font-bold">{machine.code}</p>
      <p className="text-lg font-semibold">{machine.name}</p>
      {details && <p className="text-text-muted">{details}</p>}
    </section>
  );
}

/**
 * "Find a machine" (P1.15, ADR-0028 §5): scan its QR label with the back camera, or type its code, name or label; the
 * answer comes from the tablet's reference data, with or without network. Phase 2 forms open this to pick a machine.
 */
export function MachineFinder({ find, scanner, onBack }: {
  /** Looks a scan or typed text up in the tablet's reference data (`findMachine`). */
  find: (text: string) => Promise<MachineLookup>;
  scanner: QrScanner | null;
  onBack: () => void;
}) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [lookup, setLookup] = useState<MachineLookup | null>(null);
  const [selected, setSelected] = useState<StoredAsset | null>(null);
  const [scanning, setScanning] = useState(false);
  const [cameraFailed, setCameraFailed] = useState(false);
  const video = useRef<HTMLVideoElement>(null);

  // Each keystroke looks up again: only the answer to the latest one is shown, whatever order they come back in.
  const latest = useRef(0);
  const look = useCallback(async (value: string) => {
    const asked = ++latest.current;
    const found = await find(value);
    if (asked === latest.current) {
      setLookup(found);
      setSelected(found.kind === "label" ? found.machine : null);
    }
  }, [find]);

  useEffect(() => {
    const element = video.current;
    if (!scanning || !scanner || !element) {
      return;
    }

    let stop: (() => void) | null = null;
    let active = true;
    scanner.start(element, (code) => {
      if (active) {
        setScanning(false);
        setText(code.trim());
        void look(code);
      }
    }).then((stopper) => {
      if (active) {
        stop = stopper;
      } else {
        stopper();
      }
    }).catch(() => {
      if (active) {
        setScanning(false);
        setCameraFailed(true);
      }
    });
    return () => {
      active = false;
      stop?.();
    };
  }, [scanning, scanner, look]);

  return (
    <div className="mx-auto flex min-h-screen max-w-3xl flex-col gap-5 p-4">
      <header className="flex items-center gap-3">
        <Button variant="secondary" onClick={onBack} aria-label={t("field.machines.back")}>
          <ArrowLeft aria-hidden className="size-6 rtl:-scale-x-100" />
        </Button>
        <h1 className="text-3xl font-bold">{t("field.machines.title")}</h1>
      </header>
      {scanner && !scanning && (
        <Button block onClick={() => { setCameraFailed(false); setScanning(true); }} className="min-h-20 text-xl">
          <ScanLine aria-hidden className="size-8" />
          {t("field.machines.scan")}
        </Button>
      )}
      {scanning && (
        <div className="flex flex-col gap-3">
          <video ref={video} muted playsInline aria-label={t("field.machines.camera")} className="aspect-video w-full rounded-xl bg-black object-cover" />
          <Button variant="secondary" onClick={() => { setScanning(false); }}>
            <X aria-hidden className="size-5" />
            {t("field.machines.stop")}
          </Button>
        </div>
      )}
      {cameraFailed && <p role="alert" className="font-medium text-danger">{t("field.machines.cameraFailed")}</p>}
      <TextField label={t("field.machines.search")} description={t(scanner ? "field.machines.searchHelp" : "field.machines.searchHelpNoCamera")}
        value={text} onChange={(e) => { setText(e.target.value); void look(e.target.value); }}
        autoCapitalize="characters" autoComplete="off" spellCheck={false} />
      {selected && <MachineCard machine={selected} />}
      {lookup?.kind === "unknown-label" && <p role="alert" className="font-medium text-danger">{t("field.machines.unknownLabel")}</p>}
      {lookup?.kind === "search" && text.trim() !== "" && !selected && (
        lookup.machines.length === 0
          ? <p>{t("field.machines.none")}</p>
          : (
              <ul aria-label={t("field.machines.results")} className="flex flex-col gap-2">
                {lookup.machines.map((machine) => (
                  <li key={machine.id}>
                    <Button block variant="secondary" onClick={() => { setSelected(machine); }} className="justify-start text-start">
                      <span className="font-mono font-bold">{machine.code}</span>
                      <span>{machine.name}</span>
                    </Button>
                  </li>
                ))}
              </ul>
            )
      )}
    </div>
  );
}
