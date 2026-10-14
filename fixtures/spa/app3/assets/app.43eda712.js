// Fixture SPA script (task 5.1). No build step: this file is served as written, and its name
// carries the first 8 hex digits of the SHA-256 of its own bytes, so it can sit under assets/,
// which the gateway caches for a year as immutable (contracts/s3-layout.md). Editing it means
// renaming it and updating index.html; Fixtures.Tests checks the two agree.
//
// The browser contract (contracts/http-contract.md) is everything it relies on:
//   GET  /bff/session          200 with sub, name, email, expiresAt and csrfToken; 401 without one
//   GET  /bff/login?returnUrl= navigation, never fetch
//   POST /bff/logout           form navigation, in index.html
//   /api/{name}/...            fetch; unsafe methods carry X-CSRF-TOKEN and nothing else does
// The API name comes from data-api on <main>, which mirrors apis[0].name in the app's definition.
//
// The CSRF token stays in this closure. It is never written to the page or to web storage, because
// F-05 dumps both and expects nothing token-shaped.
"use strict";

(function () {
  var main = document.getElementById("app");
  var api = "/api/" + main.getAttribute("data-api");
  var csrfToken = null;

  function show(id, text) {
    document.getElementById(id).textContent = text;
  }

  function login() {
    // returnUrl must be a local path, or the gateway makes it "/".
    window.location.assign("/bff/login?returnUrl=" + encodeURIComponent(window.location.pathname));
  }

  function loadSession() {
    return fetch("/bff/session", { credentials: "same-origin", headers: { Accept: "application/json" } })
      .then(function (response) {
        if (response.status === 401) {
          login();
          return null;
        }
        if (!response.ok) {
          throw new Error("session: " + response.status);
        }
        return response.json();
      })
      .then(function (session) {
        if (session === null) {
          return;
        }
        csrfToken = session.csrfToken;
        show("session-sub", session.sub);
        show("session-name", session.name);
        show("session-email", session.email);
        show("session-expires", session.expiresAt);
        show("session-state", "signed in");
        document.getElementById("call-api").disabled = false;
        document.getElementById("post-api").disabled = false;
      })
      .catch(function (error) {
        show("session-state", String(error.message));
      });
  }

  function callApi() {
    fetch(api + "/whoami", { credentials: "same-origin", headers: { Accept: "application/json" } })
      .then(function (response) {
        if (response.status === 401) {
          login();
          return;
        }
        return response.text().then(function (body) {
          show("api-result", response.status + " " + body);
        });
      })
      .catch(function (error) {
        show("api-result", String(error.message));
      });
  }

  function postApi() {
    fetch(api + "/echo", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrfToken },
      body: JSON.stringify({ from: main.getAttribute("data-app") }),
    })
      .then(function (response) {
        if (response.status === 401) {
          login();
          return;
        }
        // The status only: an echo body is the upstream's view of the request, and the page has
        // no reason to hold it.
        show("post-result", String(response.status));
      })
      .catch(function (error) {
        show("post-result", String(error.message));
      });
  }

  document.getElementById("call-api").addEventListener("click", callApi);
  document.getElementById("post-api").addEventListener("click", postApi);
  loadSession();
})();
