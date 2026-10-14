// The reference browser client for the platform (task 5.2). contracts/http-contract.md names it,
// with the fixture SPAs, as the reference implementation of the browser contract, and everything
// below answers to a row of that contract.
//
// A plain script, not a module, with no build step: an SPA loads it with <script src> from its own
// origin, which the baseline policy's script-src 'self' allows, and it adds one global,
// window.BffClient.
"use strict";

(function () {
  // The antiforgery request token from the last session read. It stays in this closure: it is
  // never returned, written to the page or put in web storage, because F-05 dumps all three after
  // login and expects nothing token-shaped.
  var csrfToken = null;

  // GET /bff/session: "200 with sub, name, email, expiresAt and csrfToken. 401 when there is no
  // session." Fetched, never navigated to. Resolves to the user without the token, or to null when
  // there is no session; the caller decides whether that means logging in.
  function session() {
    return fetch("/bff/session", {
      credentials: "same-origin",
      headers: { Accept: "application/json" },
    }).then(function (response) {
      if (response.status === 401) {
        csrfToken = null;
        return null;
      }
      if (!response.ok) {
        throw new Error("GET /bff/session answered " + response.status);
      }
      return response.json().then(function (body) {
        csrfToken = body.csrfToken;
        return { sub: body.sub, name: body.name, email: body.email, expiresAt: body.expiresAt };
      });
    });
  }

  // "anything but GET, HEAD and OPTIONS" (Request headers the SPA sends): only these go without
  // the token.
  var SAFE_METHODS = ["GET", "HEAD", "OPTIONS"];

  // Any method on /api/{name}/{rest}, fetched. The path is resolved the way the browser will send
  // it, so "/api/../bff/logout" or "//elsewhere/api/x" is seen for what it is and refused before a
  // request exists: the token travels to /api/** on this origin and nowhere else.
  function apiUrl(path) {
    var url = new URL(String(path), window.location.origin);
    if (url.origin !== window.location.origin || url.pathname.indexOf("/api/") !== 0) {
      throw new TypeError("BffClient.api only calls /api/** on this origin");
    }
    return url.pathname + url.search;
  }

  // Resolves to the token, reading the session first when none has been read yet.
  function token() {
    if (csrfToken !== null) {
      return Promise.resolve(csrfToken);
    }
    return session().then(function () {
      return csrfToken;
    });
  }

  // fetch() for /api/**, with the same arguments. An unsafe method carries X-CSRF-TOKEN, and the
  // token travels in that header and nowhere else (never a form field: the gateway does not read
  // a body it is about to proxy). Resolves to the upstream's Response, whatever its status.
  function api(path, init) {
    var target;
    try {
      target = apiUrl(path);
    } catch (error) {
      // Rejected rather than thrown, as fetch() itself rejects with a TypeError.
      return Promise.reject(error);
    }
    var options = Object.assign({}, init, { credentials: "same-origin" });
    var headers = new Headers(options.headers);
    var method = String(options.method || "GET").toUpperCase();

    var ready = Promise.resolve();
    if (SAFE_METHODS.indexOf(method) === -1) {
      ready = token().then(function (value) {
        if (value) {
          headers.set("X-CSRF-TOKEN", value);
        }
      });
    }

    return ready.then(function () {
      options.headers = headers;
      return fetch(target, options);
    }).then(function (response) {
      // 401 is unauthenticated or session_ended (Errors). Both mean there is no session now, and
      // /api never answers with a redirect, so the client starts the login itself and comes back
      // to this page. The response still resolves, so the caller can stop what it was doing.
      if (response.status === 401) {
        csrfToken = null;
        login();
      }
      return response;
    });
  }

  // "returnUrl must be a local path, or it becomes /". The value must start with one slash and
  // resolve, the way a browser resolves it, to this origin. A pattern on the raw string is not
  // enough: the URL parser treats "\" as "/" and strips tabs and newlines anywhere in the input
  // (WHATWG URL Standard, https://url.spec.whatwg.org/#concept-basic-url-parser), so "/\host" and
  // "/<tab>/host" both become "//host", another origin. What goes on the wire is the resolved form.
  function localPath(value) {
    if (typeof value !== "string" || value.charAt(0) !== "/") {
      return "/";
    }
    var url = new URL(value, window.location.origin);
    return url.origin === window.location.origin ? url.pathname + url.search + url.hash : "/";
  }

  // GET /bff/login?returnUrl=/path, made as a navigation: the gateway answers with a redirect to
  // the provider, which a fetch could not follow. Without an argument, returns to this page.
  function login(returnUrl) {
    var back = returnUrl === undefined
      ? window.location.pathname + window.location.search + window.location.hash
      : returnUrl;
    window.location.assign("/bff/login?returnUrl=" + encodeURIComponent(localPath(back)));
  }

  // POST /bff/logout, made as a form navigation so the browser follows the redirects through the
  // provider's logout and back to /. The form has no fields: the antiforgery token is never a form
  // field (Request headers the SPA sends), and the form-action 'self' in the baseline policy allows
  // the post.
  function logout() {
    csrfToken = null;
    var form = document.createElement("form");
    form.method = "post";
    form.action = "/bff/logout";
    form.hidden = true;
    document.body.appendChild(form);
    form.submit();
  }

  window.BffClient = Object.freeze({
    session: session,
    api: api,
    login: login,
    logout: logout,
  });
})();
