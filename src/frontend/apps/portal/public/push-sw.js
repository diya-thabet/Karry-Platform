/* The portal's push worker (ADR-0039 §5): it only shows Web Push alerts, it caches nothing (the portal is online).
 * A push carries nothing: it says that something needs a look, because a lock screen is public. Tapping the alert
 * opens the notification centre, where the person reads it behind their sign-in. */
self.addEventListener("push", (event) => {
  const english = (self.navigator.language || "").toLowerCase().startsWith("en");
  event.waitUntil(self.registration.showNotification("Karry", {
    body: english ? "Something needs a look." : "Quelque chose demande votre attention.",
    tag: "karry-notice",
    renotify: true,
  }));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  event.waitUntil(self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((windows) => {
    const open = windows.find((client) => "navigate" in client);
    return open ? open.navigate("/notifications").then((client) => client && client.focus()) : self.clients.openWindow("/notifications");
  }));
});
