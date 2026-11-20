// The fixture shell's module (#998, part of #961). It composes the fragments of its app:
//   GET /bff/session    the user and fragments, {fragmentId: signedIn | unavailable | loginRequired}
//   GET /bff/fragments  {fragments: [{fragmentId, entryUrl}]}, fetched
//   import(entryUrl)    each signedIn fragment's entry module, whose mount(element) renders it
// A fragment that is not signedIn is not mounted: the shell renders a control in its place, and the
// control starts BffClient.fragmentLogin on the user's click and never by itself (#961, "The
// fallback is user-initiated"). The reference client, /bff-client.js, is loaded before this module.
import { element } from "fixture-ui";

var main = document.getElementById("app");
var slots = document.getElementById("fragments");

function here() {
  return window.location.pathname + window.location.search + window.location.hash;
}

function fallback(slot, fragmentId, state) {
  var label = state === "unavailable" ? "Unavailable; try again" : "Sign in to " + fragmentId;
  var control = element("button", label, { type: "button", "data-fragment-login": fragmentId });
  control.addEventListener("click", function () {
    window.BffClient.fragmentLogin(fragmentId, here());
  });
  slot.appendChild(control);
}

function compose(session) {
  return fetch("/bff/fragments", { credentials: "same-origin", headers: { Accept: "application/json" } })
    .then(function (response) {
      if (!response.ok) {
        throw new Error("GET /bff/fragments answered " + response.status);
      }
      return response.json();
    })
    .then(function (body) {
      return Promise.all(body.fragments.map(function (fragment) {
        var state = session.fragments[fragment.fragmentId] || "loginRequired";
        var slot = element("div", null, { "data-slot": fragment.fragmentId, "data-state": state });
        slots.appendChild(slot);
        if (state !== "signedIn") {
          fallback(slot, fragment.fragmentId, state);
          return null;
        }
        return import(fragment.entryUrl).then(function (module) {
          module.mount(slot);
          slot.setAttribute("data-mounted", "true");
        }, function (error) {
          slot.setAttribute("data-error", String(error.message));
        });
      }));
    });
}

window.BffClient.session()
  .then(function (session) {
    if (session === null) {
      window.BffClient.login();
      return null;
    }
    document.getElementById("session-state").textContent = "signed in";
    return compose(session);
  })
  .then(function () {
    main.setAttribute("data-composed", "true");
  }, function (error) {
    document.getElementById("session-state").textContent = String(error.message);
  });
