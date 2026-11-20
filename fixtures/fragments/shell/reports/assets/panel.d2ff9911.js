// A fixture fragment's chunk (#998, part of #961), byte for byte the same in every fixture
// fragment. Its entry module at the release root imports it by a relative URL, so it resolves under
// /fragments/{fragmentId}/assets/ (O12) and is cached there as immutable; its name carries the
// first 8 hex digits of the SHA-256 of its bytes, so an assets/ URL always names the same bytes.
//
// mount() renders the fragment into the element the shell gives it and calls the fragment's own
// API through the reference client, on /fragments/{fragmentId}/api/{name}/, where the gateway
// forwards the fragment's token and never the shell's. No inline script or style, and the page is
// written with textContent only: the shell's policy is the document's.
export function mount(element, fragmentId, api) {
  var base = "/fragments/" + fragmentId + "/api/" + api;
  var section = document.createElement("section");
  section.setAttribute("data-fragment", fragmentId);
  section.setAttribute("data-chunk", new URL(import.meta.url).pathname);

  var marker = document.createElement("h2");
  marker.className = "fragment-marker";
  marker.textContent = "Fixture fragment " + fragmentId;

  var call = document.createElement("button");
  call.type = "button";
  call.textContent = "GET " + base + "/whoami";

  var result = document.createElement("pre");
  call.addEventListener("click", function () {
    window.BffClient.api(base + "/whoami").then(function (response) {
      return response.text().then(function (body) {
        result.textContent = response.status + " " + body;
      });
    }, function (error) {
      result.textContent = String(error.message);
    });
  });

  section.appendChild(marker);
  section.appendChild(call);
  section.appendChild(result);
  element.appendChild(section);
}
