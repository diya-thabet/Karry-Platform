import { useEffect, useRef, useState } from "react";
import type { GeoJSONSource, GeoJSONSourceSpecification, Map as MapLibreMap, Marker } from "maplibre-gl";
import { useTranslation } from "@karry/i18n";
import { Button } from "@karry/ui";

/** A boundary being drawn: [longitude, latitude] points, not closed (the form closes it when saving). */
export type Ring = [number, number][];

/**
 * Map style (ADR-0024): the tile provider is an owner decision (N-14); until then MapLibre's public demo style, for
 * development only.
 */
const styleUrl = (import.meta.env.VITE_MAP_STYLE_URL as string | undefined) ?? "https://demotiles.maplibre.org/style.json";

const brand = "#1F4E79";

function webGlAvailable(): boolean {
  try {
    return typeof WebGLRenderingContext !== "undefined" && document.createElement("canvas").getContext("webgl2") !== null;
  } catch {
    return false;
  }
}

const round = (value: number) => Math.round(value * 1e6) / 1e6;

function shape(ring: Ring): GeoJSONSourceSpecification["data"] {
  if (ring.length === 0) {
    return { type: "FeatureCollection", features: [] };
  }

  const geometry = ring.length >= 3
    ? { type: "Polygon" as const, coordinates: [[...ring, ...ring.slice(0, 1)]] }
    : { type: "LineString" as const, coordinates: ring };
  return { type: "FeatureCollection", features: [{ type: "Feature", properties: {}, geometry }] };
}

/**
 * The site's pin and boundary on a map (US-P1-ORG-01 AC1): a click places the pin or adds a boundary point. The
 * latitude and longitude fields next to it are the accessible and offline path; without WebGL the map is not shown.
 */
export function MapPicker(props: {
  location: { latitude: number; longitude: number } | null;
  boundary: Ring;
  editable: boolean;
  onLocation: (latitude: number, longitude: number) => void;
  onBoundary: (ring: Ring) => void;
}) {
  const { t } = useTranslation();
  const container = useRef<HTMLDivElement>(null);
  const map = useRef<MapLibreMap | null>(null);
  const marker = useRef<Marker | null>(null);
  const latest = useRef(props);
  const [mode, setMode] = useState<"pin" | "boundary">("pin");
  const modeRef = useRef(mode);
  const [ready, setReady] = useState(false);
  const [available, setAvailable] = useState(webGlAvailable);

  useEffect(() => {
    latest.current = props;
    modeRef.current = mode;
  });

  useEffect(() => {
    if (!available || !container.current) {
      return undefined;
    }

    const state = { disposed: false };
    void (async () => {
      try {
        const maplibre = await import("maplibre-gl");
        await import("maplibre-gl/dist/maplibre-gl.css");
        // MapLibre 6 starts its worker from a file next to its own module, an address it computes at run time, so the
        // build never emitted it and the production map failed ("Worker failed to load"). `?url` makes Vite ship the
        // file and gives its address.
        maplibre.setWorkerUrl((await import("maplibre-gl/dist/maplibre-gl-worker.mjs?url")).default);
        if (state.disposed || !container.current) {
          return;
        }

        const start = latest.current.location;
        const instance = new maplibre.Map({
          container: container.current,
          style: styleUrl,
          center: start ? [start.longitude, start.latitude] : [10, 34],
          zoom: start ? 13 : 4,
        });
        instance.addControl(new maplibre.NavigationControl(), "top-right");
        instance.on("load", () => {
          instance.addSource("boundary", { type: "geojson", data: shape([]) });
          instance.addLayer({ id: "boundary-fill", type: "fill", source: "boundary", paint: { "fill-color": brand, "fill-opacity": 0.2 } });
          instance.addLayer({ id: "boundary-line", type: "line", source: "boundary", paint: { "line-color": brand, "line-width": 2 } });
          setReady(true);
        });
        instance.on("click", (event) => {
          const current = latest.current;
          if (!current.editable) {
            return;
          }

          if (modeRef.current === "pin") {
            current.onLocation(round(event.lngLat.lat), round(event.lngLat.lng));
          } else {
            current.onBoundary([...current.boundary, [round(event.lngLat.lng), round(event.lngLat.lat)]]);
          }
        });
        marker.current = new maplibre.Marker({ color: brand });
        map.current = instance;
      } catch {
        setAvailable(false);
      }
    })();

    return () => {
      state.disposed = true;
      map.current?.remove();
      map.current = null;
    };
  }, [available]);

  const latitude = props.location?.latitude;
  const longitude = props.location?.longitude;
  useEffect(() => {
    const instance = map.current;
    if (!ready || !instance || !marker.current) {
      return;
    }

    if (latitude === undefined || longitude === undefined) {
      marker.current.remove();
    } else {
      marker.current.setLngLat([longitude, latitude]).addTo(instance);
    }
  }, [ready, latitude, longitude]);

  useEffect(() => {
    if (ready) {
      void map.current?.getSource<GeoJSONSource>("boundary")?.setData(shape(props.boundary));
    }
  }, [ready, props.boundary]);

  return (
    <div className="flex flex-col gap-2">
      {available
        ? <div ref={container} role="region" aria-label={t("portal.sites.map.label")} className="h-80 w-full overflow-hidden rounded-lg border-[length:var(--karry-border-width)] border-border" />
        : <p className="text-text-muted">{t("portal.sites.map.unavailable")}</p>}
      {props.editable && (
        <div className="flex flex-wrap items-center gap-2">
          {available && (
            <>
              <Button type="button" variant={mode === "pin" ? "primary" : "secondary"} aria-pressed={mode === "pin"} onClick={() => { setMode("pin"); }}>
                {t("portal.sites.map.placePin")}
              </Button>
              <Button type="button" variant={mode === "boundary" ? "primary" : "secondary"} aria-pressed={mode === "boundary"} onClick={() => { setMode("boundary"); }}>
                {t("portal.sites.map.drawBoundary")}
              </Button>
            </>
          )}
          <Button type="button" variant="ghost" disabled={props.boundary.length === 0} onClick={() => { props.onBoundary(props.boundary.slice(0, -1)); }}>
            {t("portal.sites.map.undoPoint")}
          </Button>
          <Button type="button" variant="ghost" disabled={props.boundary.length === 0} onClick={() => { props.onBoundary([]); }}>
            {t("portal.sites.map.clearBoundary")}
          </Button>
        </div>
      )}
      <p className="text-sm text-text-muted">
        {props.boundary.length === 0 ? t("portal.sites.map.noBoundary") : t("portal.sites.map.points", { count: props.boundary.length })}
      </p>
    </div>
  );
}
