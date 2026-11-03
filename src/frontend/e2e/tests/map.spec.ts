import { type APIRequestContext } from "@playwright/test";
import { expect, test } from "./guard";

// The site map (MapPicker) in the production build, under the portal's real page policy (hardening plan H-W2).
// MapLibre 6 starts a web worker from a separate file that the build did not ship until P1.18a ("Worker failed to
// load"); this loads the built map chunks on a page of the portal origin, so a missing worker or a blocked style,
// worker or image fails here. A local GeoJSON source keeps it offline and makes the worker do real work.
const portal = "http://localhost:5173";

/** The part of MapLibre's API this check uses (the E2E package does not depend on maplibre-gl). */
interface MapLibre {
  setWorkerUrl(address: string): void;
  NavigationControl: new () => object;
  Map: new (options: object) => {
    addControl(control: object, position: string): void;
    addSource(id: string, source: object): void;
    addLayer(layer: object): void;
    isSourceLoaded(id: string): boolean;
    on(event: string, listener: (event: { error: Error }) => void): void;
    once(event: string, listener: () => void): void;
  };
}

async function builtChunk(request: APIRequestContext, pattern: RegExp): Promise<string> {
  const html = await (await request.get(`${portal}/`)).text();
  const entry = /src="(\/assets\/index-[^"]+\.js)"/.exec(html)?.[1] ?? "";
  expect(entry, "the portal's entry script").not.toBe("");
  const code = await (await request.get(portal + entry)).text();
  const chunk = pattern.exec(code)?.[0] ?? "";
  expect(chunk, `a built chunk matching ${pattern.source}`).not.toBe("");
  return `/assets/${chunk}`;
}

test("the site map draws in the production build under the page policy", async ({ page, request }) => {
  const library = await builtChunk(request, /maplibre-gl-(?!worker-)[A-Za-z0-9_-]+\.js/);
  const workerAddress = await builtChunk(request, /maplibre-gl-worker-[A-Za-z0-9_-]+\.js/);
  const policy = (await request.get(`${portal}/`)).headers()["content-security-policy"] ?? "";
  expect(policy).toContain("worker-src");

  // A bare page of the portal origin with the portal's policy: the map without signing in.
  await page.route(`${portal}/map-check`, (route) =>
    route.fulfill({ status: 200, contentType: "text/html", headers: { "Content-Security-Policy": policy }, body: "<!doctype html><title>map</title><div id=map></div>" }),
  );
  await page.goto(`${portal}/map-check`);

  const outcome = await page.evaluate(
    async ({ library, workerAddress }) => {
      const maplibre = (await import(library)) as MapLibre;
      maplibre.setWorkerUrl(((await import(workerAddress)) as { default: string }).default);
      const container = document.getElementById("map");
      if (!container) {
        return "no container";
      }
      container.style.width = "400px";
      container.style.height = "300px";
      return await new Promise<string>((resolve) => {
        const map = new maplibre.Map({
          container,
          style: { version: 8, sources: {}, layers: [{ id: "paper", type: "background", paint: { "background-color": "#ffffff" } }] },
          center: [10, 34],
          zoom: 4,
        });
        map.addControl(new maplibre.NavigationControl(), "top-right");
        map.on("load", () => {
          map.addSource("site", { type: "geojson", data: { type: "Polygon", coordinates: [[[10, 34], [10.1, 34], [10.1, 34.1], [10, 34]]] } });
          map.addLayer({ id: "site-fill", type: "fill", source: "site", paint: { "fill-color": "#0b1f33", "fill-opacity": 0.2 } });
          map.once("idle", () => {
            resolve(map.isSourceLoaded("site") ? "drawn" : "source not loaded");
          });
        });
        map.on("error", (event) => {
          resolve(`error: ${event.error.message}`);
        });
        setTimeout(() => {
          resolve("timeout");
        }, 20_000);
      });
    },
    { library, workerAddress },
  );

  expect(outcome).toBe("drawn");
});
