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

  // GET /bff/session: "200 with sub, name, email, expiresAt, csrfToken and fragments. 401 when
  // there is no session." Fetched, never navigated to. Resolves to the user and the fragments map
  // without the token, or to null when there is no session; the caller decides whether that means
  // logging in. fragments maps each fragmentId of the app's entry to signedIn, unavailable or
  // loginRequired, and is {} for an app with none (#998): what the shell does with a fragment that
  // is not signedIn is its own, on a user's gesture (fragmentLogin below).
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
        return {
          sub: body.sub,
          name: body.name,
          email: body.email,
          expiresAt: body.expiresAt,
          fragments: Object.assign({}, body.fragments),
        };
      });
    });
  }

  // "anything but GET, HEAD and OPTIONS" (Request headers the SPA sends): only these go without
  // the token.
  var SAFE_METHODS = ["GET", "HEAD", "OPTIONS"];

  // registry.schema.json's fragmentId: 3 to 40 lowercase letters, digits or hyphens. A segment
  // outside it names no fragment the registry could hold.
  var FRAGMENT_ID = /^[a-z0-9-]{3,40}$/;

  // /fragments/{fragmentId}/api/ and what follows it, with the id captured.
  var FRAGMENT_API = /^\/fragments\/([^/]*)\/api\//;

  // Any method on /api/{name}/{rest}, or on a fragment's /fragments/{fragmentId}/api/{name}/{rest}
  // (#998), fetched. The path is resolved the way the browser will send it, so "/api/../bff/logout",
  // "/fragments/a/api/../../bff/logout" or "//elsewhere/api/x" is seen for what it is and refused
  // before a request exists: the token travels to those two prefixes on this origin and nowhere else.
  function apiUrl(path) {
    var url = new URL(String(path), window.location.origin);
    var fragment = FRAGMENT_API.exec(url.pathname);
    var allowed = url.pathname.indexOf("/api/") === 0 || (fragment !== null && FRAGMENT_ID.test(fragment[1]));
    if (url.origin !== window.location.origin || !allowed) {
      throw new TypeError("BffClient.api only calls /api/** or /fragments/{fragmentId}/api/** on this origin");
    }
    return url.pathname + url.search;
  }

  // The 401 codes that mean there is no session now (Errors): unauthenticated never had one, and
  // session_ended had its refresh token refused. fragment_login_required is not one of them: the
  // fragment has no usable token set and the shell's session stands, so nothing navigates and the
  // token is kept. A code the client does not know starts nothing either.
  var LOGIN_CODES = ["unauthenticated", "session_ended"];

  // The problem body's code, read from a copy so the caller still receives a Response whose body it
  // can read. A body that is not JSON has no code.
  function codeOf(response) {
    return response.clone().json().then(function (body) {
      return body && typeof body.code === "string" ? body.code : null;
    }, function () {
      return null;
    });
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

  // fetch() for /api/** and /fragments/{fragmentId}/api/**, with the same arguments. An unsafe method carries X-CSRF-TOKEN, and the
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
      if (response.status !== 401) {
        return response;
      }
      // A 401 that means there is no session: /api never answers with a redirect, so the client
      // starts the login itself and comes back to this page. The response still resolves either
      // way, so the caller can stop what it was doing.
      return codeOf(response).then(function (code) {
        if (LOGIN_CODES.indexOf(code) !== -1) {
          csrfToken = null;
          login();
        }
        return response;
      });
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

  // GET /bff/login?fragment={fragmentId}&returnUrl=/path, a navigation, for one fragment the session
  // reports as loginRequired or unavailable. For a user's gesture only: the shell renders a control
  // ("Sign in to {fragment}", "Unavailable; try again") and calls this when it is used, and nothing
  // in this client calls it, because the fallback is user-initiated and nothing escalates
  // login_required by itself (#961, "The fallback is user-initiated"). An id outside the fragmentId
  // rule is refused before anything navigates; returnUrl is held to a local path as login()'s is.
  function fragmentLogin(fragmentId, returnUrl) {
    if (typeof fragmentId !== "string" || !FRAGMENT_ID.test(fragmentId)) {
      throw new TypeError("BffClient.fragmentLogin needs a fragmentId: 3 to 40 of [a-z0-9-]");
    }
    var back = returnUrl === undefined
      ? window.location.pathname + window.location.search + window.location.hash
      : returnUrl;
    window.location.assign("/bff/login?fragment=" + encodeURIComponent(fragmentId) +
      "&returnUrl=" + encodeURIComponent(localPath(back)));
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
    fragmentLogin: fragmentLogin,
    logout: logout,
  });
})();
