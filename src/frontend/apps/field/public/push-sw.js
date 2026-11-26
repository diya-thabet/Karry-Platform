/* Web Push for Karry Terrain (ADR-0039 §5), loaded by the service worker (workbox importScripts).
 * A push carries nothing: it only says that something needs a look, because a lock screen is public. Tapping the
 * alert opens the app (or brings it forward), where the unlocked person reads their notices. */
self.addEventListener("push", (event) => {
  const english = (self.navigator.language || "").toLowerCase().startsWith("en");
  event.waitUntil(self.registration.showNotification("Karry Terrain", {
    body: english ? "Something needs a look." : "Quelque chose demande votre attention.",
    icon: "/icon.svg",
    tag: "karry-notice",
    renotify: true,
  }));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  event.waitUntil(self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((windows) => {
    const open = windows.find((client) => "focus" in client);
    return open ? open.focus() : self.clients.openWindow("/");
  }));
});
