// Tenant branding on the Karry login page (US-P1-IAM-01 AC3).
//
// The apps send users here with redirect_uri=https://<slug>.<base domain>/...; Keycloak has already checked that URI
// against the client's registered redirect URIs before rendering this page, so its origin is a Karry app. The tenant
// slug is that origin's first label; the public branding comes from the app origin's /api (anonymous, CORS "*").
// Pages rendered after a form post no longer carry redirect_uri: the origin is remembered for this tab only.
(function () {
  "use strict";

  var STORAGE_KEY = "karry.login.appOrigin";
  var SLUG = /^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])?$/;
  var COLOUR = /^#[0-9A-F]{6}$/;
  var LOGO = /^\/api\/v1\/tenancy\/public\/[a-z0-9-]+\/logo\?v=[0-9a-f]+$/;

  function remember(origin) {
    try {
      window.sessionStorage.setItem(STORAGE_KEY, origin);
    } catch (ignored) {
      // Storage blocked: branding still shows on the first page.
    }
  }

  function recall() {
    try {
      return window.sessionStorage.getItem(STORAGE_KEY);
    } catch (ignored) {
      return null;
    }
  }

  function appOrigin() {
    var redirect = new URLSearchParams(window.location.search).get("redirect_uri");
    if (redirect) {
      try {
        var origin = new URL(redirect).origin;
        remember(origin);
        return origin;
      } catch (ignored) {
        return null;
      }
    }
    return recall();
  }

  function tenantSlug(origin) {
    var labels = new URL(origin).hostname.split(".");
    var minimum = labels[labels.length - 1] === "localhost" ? 2 : 3;
    return labels.length >= minimum && SLUG.test(labels[0]) ? labels[0] : null;
  }

  // Name, logo and colour (US-P1-TEN-04). Only values of the expected shape are used: the colour becomes a CSS variable
  // (it is checked for contrast with white text by the API), the logo an image from the app origin.
  function show(origin, branding) {
    if (!branding || typeof branding.name !== "string") {
      return;
    }
    document.documentElement.setAttribute("data-karry-tenant", branding.slug);
    if (typeof branding.primaryColour === "string" && COLOUR.test(branding.primaryColour)) {
      document.documentElement.style.setProperty("--karry-brand", branding.primaryColour);
      document.documentElement.setAttribute("data-karry-brand", "");
    }
    var header = document.getElementById("kc-header-wrapper");
    if (header && !header.querySelector(".karry-tenant-name")) {
      if (typeof branding.logoUrl === "string" && LOGO.test(branding.logoUrl)) {
        var logo = document.createElement("img");
        logo.className = "karry-tenant-logo";
        logo.setAttribute("data-testid", "tenant-logo");
        logo.alt = "";
        logo.src = origin + branding.logoUrl;
        header.appendChild(logo);
      }
      var name = document.createElement("span");
      name.className = "karry-tenant-name";
      name.setAttribute("data-testid", "tenant-name");
      name.textContent = branding.name;
      header.appendChild(name);
    }
  }

  var origin = appOrigin();
  var slug = origin && tenantSlug(origin);
  if (!slug) {
    return;
  }

  var branding = window
    .fetch(origin + "/api/v1/tenancy/public/" + encodeURIComponent(slug), {
      credentials: "omit",
      headers: { Accept: "application/json" },
    })
    .then(function (response) {
      return response.ok ? response.json() : null;
    })
    .catch(function () {
      return null;
    });

  document.addEventListener("DOMContentLoaded", function () {
    branding.then(function (value) {
      show(origin, value);
    });
  });
})();
