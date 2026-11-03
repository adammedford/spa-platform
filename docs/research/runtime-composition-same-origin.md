# Runtime composition from one origin: what the gateway and the shell serve

- **Ticket:** #769, "Runtime composition from one origin: what an ESM import map, Module Federation or
  single-spa needs the gateway and the shell to serve" (`wayfinder:research`), on the map #765.
- **Date:** 2026-11-03. **Branch:** `research/runtime-composition-same-origin`, pushed and never merged,
  as the map rules.
- **Method:** primary sources only, each read on 2026-11-03: the WHATWG HTML Living Standard (page dated
  3 November 2026), the W3C CSP Level 3 Editor's Draft (27 October 2026), `mdn/content` at `35b1d90` and
  `mdn/browser-compat-data` at `6c75d9b`, the Module Federation documentation and runtime source in
  `module-federation/core` at `e323d57`, `webpack/webpack.js.org` at `866748b`, `web-infra-dev/rspack` at
  `712f3d8`, the single-spa documentation at `single-spa.js.org` `7c8b48e`, `create-single-spa` at
  `c32f9da`, SystemJS at `9647576`, `import-map-overrides` at `c0a0992`, `import-map-injector` at
  `2690334`, `@originjs/vite-plugin-federation` at `91014da` (lib 1.4.1), and this repository's
  `contracts/http-contract.md`, `contracts/s3-layout.md`, `contracts/registry.schema.json`,
  `src/Platform.Security/SecurityHeaders.cs` and `src/Platform.Hosting/Assets.cs` at `e7ffb47`.
  The HTML and CSP3 text was downloaded with `curl` and grepped, so the quotes are verbatim; the vendor
  docs were read from the repositories' raw files by three parallel readers whose quotes section 12
  lists. No container engine is on this machine, nothing used AWS, and nothing was built: this is a read
  and report. No browser probe was run, because the standard leaves no fact below ambiguous (section 12
  names what a probe would still add). Every "would" about this gateway is read from the contracts and
  the code, not from a run.

The design context is the map's charting decisions: one shell at one host owning the document and the
baseline CSP; each fragment's content served same-origin under `/fragments/{fragmentId}/**` from its own
immutable release prefix under the `s3-layout.md` cache rules; a new `GET /bff/fragments` listing each
fragment's id and live entry URL; ESM entry plus import map as the default mechanism.

## Answer in brief

1. **The baseline CSP already allows everything a same-origin ESM composition needs, with one
   exception: an import map.** `script-src 'self'` admits every same-origin script *request*: a
   parser-inserted `<script type="module" src>`, a `<script>` element the shell's code creates with a
   `src`, and a dynamic `import('/fragments/orders/…')` from a classic or module script. An import map
   is different: the standard delivers it only as an **inline** `<script type="importmap">` (an external
   `src` fires `error`), and HTML runs the CSP inline check on it with type "script" before parsing it.
   CSP3's inline check never consults `'self'`; it passes only on `'unsafe-inline'`, a nonce, a hash, or
   `'strict-dynamic'` for a non-parser-inserted element. The baseline has none of those, so **today an
   import map is blocked however it reaches the document**, whether written in the shell's `index.html`
   or inserted by the shell after fetching `GET /bff/fragments` (sections 2 and 3).
2. **(a) Where the map lives, and no document transform.** The cheapest working design needs no map at
   all: fragments are not bare specifiers, so the shell fetches `/bff/fragments` and calls
   `import(entryUrl)`; nothing is inlined and the gateway changes nothing in the document. A map is
   needed only to give shared dependencies bare names (`react`). If one is wanted, it belongs as a
   **static inline block in the shell's `index.html`**, allowed by **a `'sha256-…'` hash of its exact text
   in the shell entry's `csp.script-src`**: the schema's source grammar admits a hash source, the gateway
   appends it verbatim, and a hash keeps `'self'` in force for every request (only `'strict-dynamic'`
   suspends it). The coupling is that the hash and the release must land together. The gateway
   installing the map itself would mean buffering and rewriting a streamed `index.html` and recomputing
   its length and ETag per registry version: avoidable, and avoided. A runtime-inserted map is out
   twice over: the inline check blocks it in every browser, and a map added after a module has run
   needs the multiple-import-maps feature, which Firefox ships only behind a preference.
3. **(b) The entry URL does not carry the release, because `/fragments/{id}/**` already resolves to the
   live prefix.** Two pointer shapes keep `assets/**` immutable while the pointer revalidates: the
   registry's `fragments[].entry` names the hashed file (`/fragments/orders/assets/entry-abc123.js`), and
   `/bff/fragments` returns it; or the release holds a stable `entry.js` outside `assets/`, a one-line
   re-export of the hashed chunk, served `no-cache` with an ETag, so `/bff/fragments` returns a constant
   URL and the gateway learns nothing new per release. In both, a swap takes effect at the next document
   load, and a lazy chunk of the old release imported mid-session resolves under the new prefix and 404s
   unless the key exists there: exactly the shell's behaviour today, now per fragment.
4. **(c) Shared dependencies.** Under native ESM, module identity is the resolved URL, so "one React"
   means every consumer resolves `react` to one URL. A static map in the shell's `index.html` does that
   and makes the **shell the owner of shared versions**, which is single-spa's recommendation (a
   `shared-dependencies` partial map updated by its own pipeline). The alternative that leaves the map
   untouched is a `shared` fragment whose stable re-export URLs the map (or the fragments, by URL) point
   at; its owner then owns the versions. Under Module Federation the share scope negotiates at runtime;
   the shell can be the sole provider by declaring `react: { singleton: true }` while every fragment
   declares `import: false`. `@originjs/vite-plugin-federation` has no singleton flag at all.
5. **Module Federation 2.0 fits a same-origin path and the baseline CSP as long as four defaults hold:**
   no `getPublicPath` option on any remote (it is evaluated with `new Function`), `FEDERATION_ALLOW_NEW_FUNCTION`
   never defined, no SystemJS containers, and a static `output.publicPath: '/fragments/{id}/'`. The
   remote entry is loaded by a script element with a `src` (or `import()` for ESM containers), the
   manifest by `fetch()` under `connect-src 'self'`, styles by `<link>`. A same-origin, path-only entry
   URL works by construction in the runtime source but is undocumented. The shell needs each remote's
   `name` and manifest URL at runtime (`registerRemotes`, `loadRemote`), and the URL must contain `.json`
   to be treated as a manifest. Three traps: `dts: true` by default fetches `@mf-types.zip` from the
   remote's URL at **host build time**, which a session-gated `/fragments/` path refuses;
   `shareStrategy` defaults to `'version-first'`, which loads every registered remote at init; and
   `mf-manifest.json` and `remoteEntry.js` are stable names, so they belong outside `assets/`.
6. **single-spa needs no SystemJS and no inline script**: `registerApplication({ name, app: () =>
   import(entryUrl), activeWhen })` from a list fetched at runtime, then `start()`; single-spa-layout
   takes its routes as a JSON object. Its shared-dependency story is the import map above, with the same
   CSP consequence. SystemJS `s.js` sidesteps the inline check (its `systemjs-importmap` tag is a data
   block the browser never evaluates; modules load by `<script src>`; an external map is a same-origin
   `fetch`) at the price of `System.register` builds and a second module registry that cannot share with
   native ESM; the fuller `system.js` reaches `eval` for CSS/JSON module types, which the schema forbids.
7. **(d) CSP additions:** none for `import()` of URLs, for Module Federation at the defaults above, for
   vite-plugin-federation or for single-spa; one `'sha256-…'` source in the shell entry's `script-src`
   for a static import map; `'strict-dynamic'` is admitted by the schema and would let a runtime-inserted
   map through, but it makes the browser ignore `'self'`, so every parser-inserted shell script would
   then need a nonce or hash, and a nonce means the document transform this finding avoids. A nonce
   is never available without that transform. Separately, `style-src 'self'` blocks runtime `<style>`
   injection (CSS-in-JS) under every mechanism; that is my reading of CSP3's inline style check, not a
   vendor statement.
8. **Caching under `s3-layout.md`:** hashed chunks and hashed entries under `assets/` (a year,
   immutable); the entry pointer, `mf-manifest.json`, a stable `remoteEntry.js` and `index.html` outside
   `assets/` (`no-cache` with an ETag, revalidated per use); the import map inside `index.html` inherits
   its revalidation; `/bff/fragments` is an API response and should say `no-store`, as `/bff/session`
   does by being identity-bound. vite-plugin-federation emits `assets/remoteEntry.js` by default, which
   is safe only because each release has its own prefix and the URL comes from `/bff/fragments`.

## 1. What the baseline allows today (read from the repository)

`contracts/http-contract.md` sets, on every response:

```text
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self';
connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none';
form-action 'self' <identity provider origin>
```

No nonce, no hash, no `'strict-dynamic'`. An entry's `csp` adds sources to those ten directives; `*` and
`'unsafe-eval'` are rejected everywhere, `'unsafe-inline'` and `data:` in `script-src`
([http-contract][hc]; `registry.schema.json` `$defs/csp`, `cspSource`, `scriptSrcSource`). The source
grammar is otherwise `^\S+$`, so `'sha256-…'`, `'nonce-…'` and `'strict-dynamic'` pass the schema.
`SecurityHeaders.Build` builds one policy string per app definition from the baseline plus the entry's
additions; its only filter, `CanSplice`, drops a source holding `;`, `,`, whitespace or a control
character ([SecurityHeaders.cs][sh]). Nothing in the gateway interprets keyword semantics, so a hash
source reaches the header verbatim.

What that means for same-origin module loading, by CSP3's two checks:

- **Requests** (`<script src>`, `import()`, `<link rel="modulepreload">`) go through the pre-request
  check, where `'self'` matches a same-origin URL. Parser-inserted or not does not matter unless
  `'strict-dynamic'` is present ([CSP3 pre-request][csp-prereq]).
- **Inline blocks** go through "Should element's inline behavior be blocked": "Their behavior will be
  blocked unless every policy allows inline script, either implicitly by not specifying a script-src (or
  default-src) directive, or explicitly, by specifying "unsafe-inline", a nonce-source or a hash-source
  that matches the inline block" ([CSP3 script-src][csp-scriptsrc]). The matching algorithm also returns
  "Matches" for `'strict-dynamic'` when "type is "script", and element is not parser-inserted"
  ([CSP3 match element][csp-match]). `'self'` is never consulted.

Assets are served with `private, max-age=31536000, immutable` under `assets/` and `private, no-cache`
with the object's ETag elsewhere; `index.html` is streamed with `CopyToAsync` and
`ContentLength = stored.Length` ([Assets.cs][assets]). Every asset needs a session (ADR-0008), which is
why every response is `private`, and why a build-time tool cannot fetch from `/fragments/` (section 6).

## 2. Import maps: what the standard says

**Delivery is inline only.** "Import maps are delivered via inline script elements with their type
attribute set to "importmap", and with their child text content containing a JSON representation of the
import map" ([HTML import maps][html-im]). In "prepare the script element", a `src` on one is an error:
"If el's type is "importmap" or "speculationrules", then queue an element task ... to fire an event named
error at el, and return. External import maps and speculation rules are not currently supported. See
WICG/import-maps issue #235" ([HTML prepare][html-prep]). MDN agrees: "The src, async, nomodule, defer,
crossorigin, integrity, and referrerpolicy attributes must not be specified" ([MDN importmap][mdn-im]).

**The CSP inline check runs on it.** The same algorithm first classifies the element ("if the script
block's type string is an ASCII case-insensitive match for the string "importmap", then set el's type to
"importmap""), then, before any per-type handling: "Let cspType be "script speculationrules" if el's type
is "speculationrules"; otherwise, "script". If el does not have a src content attribute, and the Should
element's inline behavior be blocked by Content Security Policy? algorithm returns "Blocked" when given
el, cspType, and source text, then return." Only afterwards: ""importmap": Let result be the result of
creating an import map parse result given source text and base URL" and, at execution, "Register an
import map" ([HTML prepare][html-prep]; verified in the downloaded text, section 12). An unknown type
(`systemjs-importmap`, `injector-importmap`) returns at the classification step ("No script is executed,
and el's type is left as null") and never reaches the check: it is a data block for a library to read.

**Multiple maps and timing.** "A Document can have multiple import maps processed, which can happen
either before or after any modules have been imported, e.g., via import() expressions or script elements
with their type attribute set to "module". The merge existing and new import maps algorithm ensures that
new import maps cannot define the module resolution for modules that were already defined by past import
maps, or for ones that were already resolved" ([HTML import maps][html-im]). The resolved module set
"ensures that import map rules that impact the specifier in its referrer's scope cannot be defined after
its initial resolution" ([HTML resolved set][html-resolved]); a rule for a specifier that an already
resolved one starts with is removed, with an optional console warning ([HTML merge][html-merge]). So a
late map can add new names but cannot rebind or repoint anything already used. Per MDN's compat data,
`importmap` is Chrome 89, Firefox 108, Safari 16.4; `integrity` Chrome 127, Firefox 138, Safari 18;
**multiple import maps Chrome 133, Safari 18.4, Firefox 150 only behind `dom.multiple_import_maps.enabled`**
([BCD script.json][bcd]). MDN's current wording: maps "must be declared and processed before any
`<script>` elements that import modules using specifiers declared in the map" ([MDN importmap][mdn-im]).

**What a map does and does not touch.** It rewrites module specifiers in `import` statements and
`import()`; "it does not apply to the path specified in the src attribute of a `<script>` element or to
modules loaded into workers or worklets" ([MDN importmap][mdn-im]). Map addresses resolve against the
document's base URL; a key and value ending in `/` remap a prefix; `scopes` select a sub-map by the URL
of the importing script; `integrity` attaches subresource integrity to a resolved URL
([MDN modules guide][mdn-mod]). The guide's cache pattern is the one this map wants: a stable bare name
mapped to a hashed file, so "If dependency_script changes, then its hash contained in the file name
changes as well. In this case, we only need to update the import map" ([MDN modules guide][mdn-mod]).

**`import()` without a map.** It "is always available, allowing you to import modules into non-module
environments", resolves its specifier by "the same algorithm as static import declarations", and
returns a promise of the namespace ([MDN import()][mdn-dyn]). A relative specifier resolves against the
referring script's base URL, which for an external script is its own URL and for an inline script the
document base URL ([HTML script base URL][html-base]). A URL specifier such as
`/fragments/orders/assets/entry-abc123.js` needs no map.

## 3. ESM entry plus import map on this gateway: (a) where the map lives

Three designs, in order of cost.

**A. No import map.** The shell's bootstrap module calls `fetch('/bff/fragments')`, then
`import(entryUrl)` per fragment, and the entry module exports whatever mount contract the teams agree.
Each fragment's own chunks are relative imports from its entry (`./chunk-…js`, resolved under
`/fragments/{id}/assets/`), so nothing is bare. Requires nothing of the gateway beyond `/bff/fragments`
and the `/fragments/` route; no change to the CSP; no document transform. Shared dependencies are then
imported by URL (section 5) or bundled per fragment. This is the design to default to for the
fragments themselves even if a map is added for shared libraries.

**B. A static map in the shell's `index.html`, allowed by a hash.** The shell release's `index.html`
carries `<script type="importmap">{…}</script>` before its module script. The shell's registry entry adds
`'sha256-<base64 of the block's exact child text>'` to `csp.script-src`. Facts that make this work:

- The hash is computed over the element's source text ("UTF-8 encode ... JavaScript string converting on
  source"; "When generating the hash, don't include the `<script>` tags and note that capitalization and
  whitespace matter") ([CSP3 match element][csp-match]; [MDN script-src][mdn-ss]). A hash source does not
  depend on the element's `type`.
- A hash source leaves `'self'` effective for requests; only `'strict-dynamic'` makes "host-source and
  scheme-source expressions, as well as the "'unsafe-inline'" and "'self'" keyword-sources ... ignored"
  ([CSP3 strict-dynamic usage][csp-sd]). A hash does make `'unsafe-inline'` ignored, which the schema
  rejects in `script-src` anyway.
- The schema admits it and `SecurityHeaders` appends it (section 1).

The cost is a coupling: the hash is a property of one shell release's `index.html`, and it lives in the
registry entry beside `release`. A shell activation that changes the map must change `csp.script-src`
in the same registry write, or the map is blocked until they agree. (Suggestion, not a finding:
`spactl` could compute the hash from the release's `index.html` at publish and write both.) The map's
content then changes only with the shell's release, which is why fragments should be imported by URL
(design A) and the map should hold only shared-dependency names, or stable fragment pointers that a
fragment swap does not move (section 4).

**C. A map the shell installs at runtime from `GET /bff/fragments`.** Blocked by the baseline, and
fixing that costs more than B:

- The element the shell creates is inline and non-parser-inserted. Under `script-src 'self'` the inline
  check returns Blocked in every conforming browser (section 2). Firefox's missing multiple-map support
  is a second, separate limit: it bites only when the map is added after a module has run. A classic
  bootstrap script could insert the first map before any `import()`, even in Firefox, and would still be
  blocked by CSP.
- `'strict-dynamic'` would admit it ("If type is "script", and element is not parser-inserted, return
  "Matches"") but suspends `'self'`, so the shell's own parser-inserted `<script type="module" src>` and
  any `<link rel="modulepreload">` would then need a nonce or hash. Named, not recommended.
- A nonce would admit it, but a nonce is per response and must appear on the element in the document,
  so the gateway would have to generate it and rewrite the shell's `index.html` on every navigation.
  That is the document transform: buffer the streamed object, insert, recompute `ContentLength`, and
  replace the object's ETag with one that also covers the inserted text, or `no-cache` revalidation
  returns a stale 304. Every other design here avoids it.
- The `import-map-overrides` and `import-map-injector` tools single-spa's generator uses do the same
  runtime insertion and are blocked for the same reason; their own docs recommend removing overrides
  from production and give only a style nonce option ([imo security][imo-sec]; [imi README][imi]).

**Verdict on (a):** the map, if any, lives in the shell's `index.html` and is allowed by a hash in the
shell entry's `csp`; the gateway never transforms the document; fragment entries are imported by URL from
`/bff/fragments`, which the shell can fetch and act on with no map at all.

## 4. (b) How the entry URL carries its release

It does not, and need not: `/fragments/{id}/**` resolves to `apps/{appId}/fragments/{id}/releases/{release}/**`
for the live `release`, so the release is a gateway-side fact and the browser only ever sees paths. The
cache rules by path then do the work: anything under `assets/` is hashed by the build and immutable;
anything else revalidates. Two shapes for the pointer:

| Shape | `/bff/fragments` returns | What must know the hashed name | Cache behaviour |
| --- | --- | --- | --- |
| **Registry field** `fragments[].entry` | `/fragments/orders/assets/entry-abc123.js` | `spactl fragment release` writes `release` and `entry` together | Entry immutable; the pointer is the JSON, fetched per document load |
| **Stable re-export** `entry.js` at the release root | `/fragments/orders/entry.js`, constant | Nobody outside the fragment's build (`export * from './assets/entry-abc123.js'`) | Entry `no-cache` + ETag, one conditional GET per load; chunks immutable |

Both honour the write-once rule (`entry.js` is one more key in the release). The second keeps
`/bff/fragments` free of per-release data, at one revalidation per load; the first lets the shell
`modulepreload` the hashed entry directly. Either way, the import map, if present, can name the stable
URL and never change on a fragment swap.

Two consequences to write into the spec, whichever shape wins:

- **A swap takes effect at the next document load.** The module map is per document; an already
  imported entry stays. A fragment's lazy chunk imported after the swap resolves relative to the entry's
  URL, that is under the **new** prefix, and 404s unless the new release holds that key. The shell has
  this property today; a fragment swap gives it per fragment, and "a fragment swap leaves the shell's
  release alone" holds at the HTTP level while a mid-session fragment may still break itself.
- **The fragment's build must emit paths that hold under `/fragments/{id}/`.** For Vite that is
  `base: '/fragments/orders/'` (the id is stable, so an absolute base is fine) or a relative base; for
  webpack and Rspack `output.publicPath: '/fragments/orders/'`; `'auto'` works when the entry is in the
  output root (section 6). `import.meta.url` inside the entry already points under the prefix.

## 5. (c) Shared dependencies, per mechanism

**Native ESM (and single-spa on native ESM).** A module's identity is its resolved URL, so a single
React instance requires every importer to end at one URL. Two ways to express it:

- **A bare name in the shell's static map** (`"react": "/assets/react-19.1.0-h4x.js"` or a stable
  `/shared/react.js`): each fragment's build marks `react` external and emits `import … from "react"`.
  The map is the shell release's, so the **shell owns the shared versions**, exactly single-spa's
  recommendation: "Each large shared-dependency (ie, the react, vue, or angular libraries) should also be
  an in-browser module"; "To make the shared dependencies available as in-browser modules, they must be
  present in your import map. A good way of managing them is to create a repository called
  `shared-dependencies` that has a partial import map" ([single-spa recommended][ssa-rec]). Needs design
  B's hash. `scopes` could pin a different version for one fragment's path, which is the opposite of a
  singleton, so it is a tool for migration, not steady state.
- **A URL, no map:** each fragment imports `/fragments/shared/react.js` (a `shared` fragment entry whose
  owner owns the versions, with stable re-exports as in section 4), or the shell exports its React and
  hands it to a fragment's `mount(el, { react })`. No CSP change; the version contract moves from a map
  to a URL convention.

**Module Federation.** Sharing is negotiated at runtime in a share scope: "A share scope can hold
several versions of the same module at once ... a consumer gets the highest one that satisfies its own
requiredVersion ... `singleton: true` gives up that guarantee to keep one instance, and warns when the
chosen version does not satisfy a consumer" ([webpack MF plugin][wp-mf]). "If set to `false`, this
shared will not be packaged into the product, and only the `shared` provided by the consumer will be
used" (`import`, [MF shared][mf-shared]). So the shell can be the sole provider: shell
`react: { singleton: true, requiredVersion }`, every fragment `react: { singleton: true, import: false,
requiredVersion: '^X' }`; a mismatch is a warning, or an error with `strictVersion`. Whichever build's
version wins serves its own chunk from its own `publicPath`; with `import: false` on the fragments the
only copy is the shell's. `shareStrategy: 'version-first'` (the default) "**All remotes entry files will
be automatically loaded during initialization**"; `'loaded-first'` loads on demand ([MF shareStrategy][mf-ss]),
which is the one that fits discovery from `/bff/fragments`.

**`@originjs/vite-plugin-federation`.** Sharing goes through a page global `globalThis.__federation_shared__`,
the host "will generate a shared chunk no matter what", and a remote with `import: false` has no
fallback; `singleton` and `strictVersion` exist only as commented-out type members, so there is no
singleton enforcement ([vpf README][vpf]; [vpf types][vpf-types]).

**single-spa's guidance across both:** "We currently recommend only using import maps, although we have
no objection to module federation" and "We do not recommend sharing some third-party dependencies via
import map and others via module federation" ([single-spa recommended][ssa-rec]).

**Ownership, for the map's open bullet:** with a static map the shell owns shared versions and a shared
bump is a shell release; with a `shared` fragment its owner does and a bump is that fragment's release;
with Module Federation the provider build (the shell, under `import: false`) does. In every case the
singleton is a convention the teams keep, not something the gateway can prove.

## 6. Module Federation: webpack 5, Rspack, Module Federation 2.0

**What a remote exposes.** `filename` defaults to `remoteEntry.js` ([MF filename][mf-filename]). With
`@module-federation/enhanced` the plugin also emits `mf-manifest.json` and `mf-stats.json` on every build
unless `manifest: false` ([MF manifest][mf-manifest]; [enhanced plugin source][mf-plugin-src]). The
manifest holds `id`, `name`, `metaData` (with either `publicPath` or a `getPublicPath` string, the
`remoteEntry` file name and type, `globalName`), `shared`, `remotes` and `exposes`, each expose listing
its sync and async js and css assets ([MF manifest fields][mf-fields]). Its name is a plain string with
no hashing option; the remote entry's name is whatever single file the container chunk emitted, so a
hashed `remoteEntry.[contenthash].js` is discoverable only through the manifest
([StatsManager.ts][mf-stats]).

**How the host names a remote.** Build time: `remotes: { orders: 'orders@http://host/mf-manifest.json' }`,
where "`entry` can be either `mf-manifest.json` or `remoteEntry.js`" and the manifest form adds type
hints, preloading and devtools ([MF remotes][mf-remotes]). Runtime: `registerRemotes([{ name, entry }])`
then `loadRemote('orders/Widget')`; `init` is marked "Use with caution" in favour of
`createInstance`/`getInstance` ([MF runtime API][mf-api]). The runtime decides manifest-versus-script by
whether the entry URL contains `.json` ([generateSnapshotFromManifest.ts][mf-snap]; `MANIFEST_EXT`).
Plain webpack 5 has no manifest: the host knows the exact `remoteEntry.js` URL at build time, or plumbs a
runtime URL through a `promise new Promise(...)` remote that must "resolve an object which contains the
get/init API" ([webpack MF concepts][wp-concepts]). Rspack's built-in plugin is MF 1.5 with the same
option surface, `manifest` included since 1.6.1; MF 2.0 on Rspack is `@module-federation/enhanced/rspack`
([rspack MF guide][rs-guide]; [rspack plugin][rs-plugin]).

**A same-origin path as the entry.** Not documented either way (every example is `http://localhost`).
In the runtime source it works by construction: `inferAutoPublicPath` is string arithmetic that turns
`/fragments/orders/mf-manifest.json` into `/fragments/orders/`; the `https:` prefixing of a scheme-less
URL happens only outside the browser; URL comparison strips the scheme and host; the manifest is read
with `fetch(manifestUrl, {})`, which takes a relative URL ([generateSnapshotFromManifest.ts][mf-snap];
[snapshot plugin][mf-snapplugin]; [SnapshotHandler.ts][mf-handler]). Treat it as a prototype item.

**publicPath.** `metaData.publicPath` is `output.publicPath` copied verbatim; `'auto'` is resolved by
the host from the manifest URL's directory ([StatsManager.ts][mf-stats];
[generateSnapshotFromManifest.ts][mf-snap]), while the remote's own chunk loader infers from
`document.currentScript` ([webpack public path][wp-pp]). The two agree only when the manifest sits in
the remote's output root; a static `'/fragments/{id}/'` removes the question. Never the `getPublicPath`
option: "the string in `getPublicPath` will be executed via `new Function`" ([MF getPublicPath][mf-gpp]),
which needs `'unsafe-eval'`, rejected by the schema.

**How it loads, and the CSP.** The remote entry and every expose, shared and preload script is a
`document.createElement('script')` with `script.src = info.url`, appended to `document.head`; nothing
sets inline text ([dom.ts][mf-dom]). `remoteType` defaults to `'script'`; `'module'`/`'esm'` containers
load with `import(url)` ([MF remoteType][mf-rt]; [load.ts][mf-load]). The manifest is `fetch`ed
(`connect-src`). CSS arrives as `<link>` (`style-src`). The only `new Function` sites in the runtime are
`getPublicPath` evaluation, the ESM path when a build defines `FEDERATION_ALLOW_NEW_FUNCTION` (no
definer found in the repository), and SystemJS entries ([utils.ts][mf-utils]; [load.ts][mf-load]). So
`default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'` suffices at the defaults.
ESM containers (`library: { type: 'module' }`) are the norm for Vite remotes and exist for webpack, whose
docs still call module library output "not fully supported yet" ([webpack output][wp-out]).

**Build time versus runtime.** Build time: the shell's `shared` declarations, `shareStrategy`, and, if
static, the `remotes` map. Runtime: `registerRemotes` from `/bff/fragments`, which must then carry each
remote's `name` (the container's name, which the manifest also states) and its manifest URL. Whether a
hyphenated `fragmentId` (`^[a-z0-9-]+$`) is acceptable as an MF `name` was not checked. `dts: true` by
default makes the host "automatically pull the type file of `remotes`" (`@mf-types.zip`) at build time
([MF dts][mf-dts]), from a URL that here needs a session cookie (ADR-0008): set `dts: false` or point the
host's build at the fragment's artifact store, not the gateway.

**Caching layout.** `mf-manifest.json` at the release root (revalidated per load);
`filename: 'assets/remoteEntry.[contenthash].js'` to make the entry immutable and reach it through the
manifest; or a plain `remoteEntry.js` at the root, revalidated. Neither docs set discusses cache headers.

## 7. single-spa, single-spa-layout, SystemJS

- "You do not have to use SystemJS when using single-spa" ([single-spa config][ssa-cfg]); the
  recommended setup is "in-browser ES modules + import maps (or SystemJS to polyfill these)"
  ([single-spa recommended][ssa-rec]). "The browser and SystemJS maintain separate module registries.
  This means that you can't share imports between SystemJS and native modules" ([single-spa Vite][ssa-vite]).
- The root config is `registerApplication({ name, app: () => import(url), activeWhen })` then `start()`;
  `app` is "a function that returns the resolved application (Promise or not)" ([single-spa API][ssa-api]).
  An application exports `bootstrap`, `mount`, `unmount`, each returning a promise
  ([single-spa applications][ssa-apps]). Registration "most commonly occurs inside of the single-spa
  config but does not have to", and `start()` may wait for "an initial AJAX request" ([single-spa config][ssa-cfg]):
  a shell can fetch `/bff/fragments`, register each row, then start. `activeWhen` is a shell-side
  routing decision the list may or may not carry. A documented "fetch the app list at runtime" pattern:
  not found; the nearest is the "manifest file ... which controls which versions ... are 'live'" option
  ([single-spa separating][ssa-sep]).
- single-spa-layout: "You may define layouts as either HTML templates or JSON objects ... Both HTML and
  JSON layouts have the same feature set" ([layout definition][ssl-def]); `constructRoutes` takes the
  JSON, `constructApplications` takes a `loadApp` returning `() => import(url)` ([layout API][ssl-api]).
  No inline script is required; the only nonce in its docs is the server-rendering one.
- The official generator's `index.html` is instructive as a warning: its own CSP meta grants
  `'unsafe-inline' 'unsafe-eval'`, it loads `import-map-overrides` and `import-map-injector` from a CDN,
  and bootstraps with an inline `<script>` ([create-single-spa template][cssa]). Under this gateway every
  one of those moves to an external same-origin module, and the injector's final `<script type="importmap">`
  is the runtime-inserted map of section 3C.
- single-spa's FAQ: "module imports specifically relate to CSP `script-src`" and "Import-maps are also
  governed by CSP" ([single-spa FAQ][ssa-faq]).
- SystemJS: `s.js` (2.8 KB) loads `System.register` modules, "the CSP-compatible SystemJS module format",
  by `<script src>` (`createScript` in `script-load.js`), reads inline or external
  `<script type="systemjs-importmap">` (external by `fetch`), supports composing multiple maps (marked
  experimental) and a non-standard `System.addImportMap`; "The import map is fixed as soon as the first
  `System.resolve` ... is called" ([SystemJS README][sjs]; [SystemJS import maps][sjs-im]). The fuller
  `system.js` wraps CSS/JSON/Wasm module types in a synthesized source it `eval`s ([module-types.js][sjs-mt]),
  so only `s.js` fits a policy without `'unsafe-eval'`. Price: System.register output from every
  fragment build and no sharing with native modules.

## 8. `@originjs/vite-plugin-federation`

- Remote: `federation({ name, filename: 'remoteEntry.js', exposes, shared })`; the entry is emitted with
  an explicit unhashed `fileName` under `build.assetsDir` (default `assets`), so `dist/assets/remoteEntry.js`
  ([expose-production.ts][vpf-expose]). Under this gateway that lands in the immutable prefix; it is
  safe only because the release has its own prefix and the URL comes from `/bff/fragments`, or move it
  with `assetsDir`/`filename`. "the Remote side requires the RemoteEntry.js package to be generated using
  `vite build`"; `build.target` must allow top-level await (`esnext`) ([vpf README][vpf]).
- Host: `remotes: { orders: '/fragments/orders/assets/remoteEntry.js' }` at build time, or
  `virtual:__federation__`'s `setRemote(name, { url, format, from })` and `getRemote(name, module)` for "the
  list of these remotes asynchronously" ([vpf README][vpf]). With `format: 'esm'` (default) the host
  calls native `import(url)`; `'var'` uses a `<script src>`; the URL string is passed straight through,
  so a root-relative path works in the source though the docs show only absolute URLs
  ([remote-production.ts][vpf-remote]; [utils/index.ts][vpf-utils]).
- No `eval`, no `new Function`, no inline script or style in `packages/lib/src/prod`; CSS is a `<link>`.
  CSP-neutral under the baseline. No singleton (section 5).

## 9. Caching under `s3-layout.md`

| File | Where | Rule | Why |
| --- | --- | --- | --- |
| Hashed entry and chunks | `assets/**` | a year, `immutable` | The build hashes them; identity by URL |
| Entry pointer `entry.js` (section 4) | release root | `no-cache` + ETag | Stable name, changes per release |
| `mf-manifest.json`, stable `remoteEntry.js` | release root | `no-cache` + ETag | Stable names; MF docs give no cache guidance |
| Import map | inside the shell's `index.html` | the shell's `no-cache` + ETag | Inline is the only delivery |
| `GET /bff/fragments` | API | `no-store` (recommendation) | Identity-bound like `/bff/session`; the live pointer must not be served from a browser cache after a swap |
| Vite default `assets/remoteEntry.js` | `assets/**` | a year, `immutable` | Safe per release prefix; relocate if a stable URL is wanted |

## 10. (d) CSP additions, per mechanism

| Mechanism | `script-src` | Other directives | Condition |
| --- | --- | --- | --- |
| ESM, `import(entryUrl)` from `/bff/fragments` (3A) | none | none | Entry and chunks same-origin |
| ESM with a static import map (3B) | `'sha256-…'` of the block's text, in the shell entry's `csp` | none | Hash and release land together |
| ESM with a runtime-inserted map (3C) | blocked; `'strict-dynamic'` admits it but suspends `'self'`; a nonce needs a document transform | none | Not recommended |
| Module Federation 2.0, webpack, Rspack | none | `connect-src 'self'` for the manifest (already set); `style-src 'self'` for `<link>` | No `getPublicPath`, no `FEDERATION_ALLOW_NEW_FUNCTION`, no SystemJS, static `publicPath` |
| vite-plugin-federation | none | none | `format: 'esm'` or `'var'` |
| single-spa on native ESM | none (map, if any, as 3B) | none | Bootstrap in an external module, no injector |
| single-spa on SystemJS `s.js` | none | `connect-src 'self'` for an external map | Not `system.js` with CSS/JSON module types (`eval`) |
| Any, with CSS-in-JS runtime `<style>` injection | n/a | `style-src` has no allowance; inline styles blocked | My reading of CSP3's inline style check; constructable stylesheets or emitted CSS files avoid it |

## 11. What `GET /bff/fragments` must carry

- For ESM and single-spa: `fragmentId` and `entryUrl`, nothing more; single-spa's `activeWhen` is the
  shell's routing choice and may stay in the shell.
- For Module Federation: the container `name` and the manifest URL (containing `.json`); the shell
  calls `registerRemotes` with both. Whether `fragmentId` can double as `name` is unverified.
- For vite-plugin-federation: `name`, `url`, and `format`/`from` for `setRemote`.
- In every case the response is per document load and should not be cached (section 9).

## 12. What I executed and what I read

**Executed** (this session, 2026-11-03):
- `gh issue edit 769 --add-assignee @me`; `gh issue view 769`; `gh issue view 765`.
- `curl -sL` of `https://html.spec.whatwg.org/multipage/scripting.html`,
  `.../webappapis.html` and `https://w3c.github.io/webappsec-csp/` into the scratchpad, then a Python
  tag-strip and search of those three texts to verify, verbatim, the step order in "prepare the script
  element" (classification, then the inline check with cspType "script", then the `src` error for
  `importmap`, then parse and register), the import maps section's multiple-map sentence, the resolved
  module set, the merge algorithm, and CSP3's "Inline script blocks MUST pass through", "Does a source
  list allow all inline behavior", and the `'strict-dynamic'` non-parser-inserted match. The files were
  left in the scratchpad.
- `grep`/`sed`/`python3 -c` over this worktree: `contracts/http-contract.md`, `contracts/s3-layout.md`,
  `contracts/registry.schema.json` (`csp`, `cspSource`, `scriptSrcSource`, `appId`, `release`),
  `src/Platform.Security/SecurityHeaders.cs` (`Baseline`, `Build`, `CanSplice`),
  `src/Platform.Hosting/Assets.cs` (cache constants, streaming), `docs/adr/0008`, `.github/steering.py`
  (`docs/research/` is not a steering path).

**Read only:** every vendor source, through three parallel readers working from raw repository files
at the shas in the Method line, whose quotes this document carries; MDN and its compat data likewise.
Not done: no build, no browser, no AWS, no container, no probe, no change outside this file.

**Open, for the prototype or the first real run:** browser conformance was not observed, so whether
Chrome, Firefox and Safari all apply the inline check to `type="importmap"` and accept a `'sha256-…'`
match of the map's exact text is read from the standards, not seen; whether MF 2.0's runtime accepts a
path-only entry URL in a real page (by construction in the source, undocumented); whether a hyphenated
`fragmentId` is a valid MF container `name`; and the `styled-components`/`emotion` consequence of
`style-src 'self'`, which is a reading of CSP3, not a vendor statement.

[hc]: ../../contracts/http-contract.md
[sh]: ../../src/Platform.Security/SecurityHeaders.cs
[assets]: ../../src/Platform.Hosting/Assets.cs
[html-im]: https://html.spec.whatwg.org/multipage/webappapis.html#import-maps
[html-prep]: https://html.spec.whatwg.org/multipage/scripting.html#prepare-the-script-element
[html-resolved]: https://html.spec.whatwg.org/multipage/webappapis.html#resolved-module-set
[html-merge]: https://html.spec.whatwg.org/multipage/webappapis.html#merge-existing-and-new-import-maps
[html-base]: https://html.spec.whatwg.org/multipage/webappapis.html#concept-script-base-url
[csp-prereq]: https://w3c.github.io/webappsec-csp/#script-pre-request
[csp-scriptsrc]: https://w3c.github.io/webappsec-csp/#directive-script-src
[csp-match]: https://w3c.github.io/webappsec-csp/#match-element-to-source-list
[csp-sd]: https://w3c.github.io/webappsec-csp/#strict-dynamic-usage
[mdn-im]: https://github.com/mdn/content/blob/35b1d907e22f58fcd01fc5d3d22f1e73089c7f3d/files/en-us/web/html/reference/elements/script/type/importmap/index.md
[mdn-mod]: https://github.com/mdn/content/blob/35b1d907e22f58fcd01fc5d3d22f1e73089c7f3d/files/en-us/web/javascript/guide/modules/index.md
[mdn-dyn]: https://github.com/mdn/content/blob/35b1d907e22f58fcd01fc5d3d22f1e73089c7f3d/files/en-us/web/javascript/reference/operators/import/index.md
[mdn-ss]: https://github.com/mdn/content/blob/35b1d907e22f58fcd01fc5d3d22f1e73089c7f3d/files/en-us/web/http/reference/headers/content-security-policy/script-src/index.md
[bcd]: https://github.com/mdn/browser-compat-data/blob/6c75d9bac352ec1e784bf774f90e9bac7b7befd6/html/elements/script.json
[mf-remotes]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/remotes.mdx
[mf-manifest]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/manifest.mdx
[mf-filename]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/filename.mdx
[mf-fields]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/guide/advanced/manifest-fields.mdx
[mf-api]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/guide/runtime/runtime-api.mdx
[mf-shared]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/shared.mdx
[mf-ss]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/shareStrategy.mdx
[mf-gpp]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/getpublicpath.mdx
[mf-rt]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/remotetype.mdx
[mf-dts]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/apps/website-new/docs/en/configure/dts.mdx
[mf-plugin-src]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/enhanced/src/lib/container/ModuleFederationPlugin.ts
[mf-stats]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/manifest/src/StatsManager.ts
[mf-snap]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/sdk/src/generateSnapshotFromManifest.ts
[mf-snapplugin]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/runtime-core/src/plugins/snapshot/index.ts
[mf-handler]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/runtime-core/src/plugins/snapshot/SnapshotHandler.ts
[mf-dom]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/sdk/src/dom.ts
[mf-load]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/runtime-core/src/utils/load.ts
[mf-utils]: https://github.com/module-federation/core/blob/e323d5722394a2d913c386aafe12346b2105e973/packages/sdk/src/utils.ts
[wp-mf]: https://github.com/webpack/webpack.js.org/blob/866748b59b6f4db0c5a2448bbee81fa5c080982e/src/content/plugins/module-federation-plugin.mdx
[wp-concepts]: https://github.com/webpack/webpack.js.org/blob/866748b59b6f4db0c5a2448bbee81fa5c080982e/src/content/concepts/module-federation.mdx
[wp-pp]: https://github.com/webpack/webpack.js.org/blob/866748b59b6f4db0c5a2448bbee81fa5c080982e/src/content/guides/public-path.mdx
[wp-out]: https://github.com/webpack/webpack.js.org/blob/866748b59b6f4db0c5a2448bbee81fa5c080982e/src/content/configuration/output.mdx
[rs-guide]: https://github.com/web-infra-dev/rspack/blob/712f3d8efe30c59bb92156402221ee5f6f73861f/website/docs/en/guide/advanced/module-federation.mdx
[rs-plugin]: https://github.com/web-infra-dev/rspack/blob/712f3d8efe30c59bb92156402221ee5f6f73861f/website/docs/en/plugins/module-federation-plugin.mdx
[ssa-cfg]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/single-spa-config.md
[ssa-rec]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/recommended-setup.md
[ssa-vite]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/ecosystem-vite.md
[ssa-api]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/api.md
[ssa-apps]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/building-applications.md
[ssa-sep]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/separating-applications.md
[ssa-faq]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/faq.md
[ssl-def]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/layout-definition.md
[ssl-api]: https://github.com/single-spa/single-spa.js.org/blob/7c8b48ee7bd0fd08451be50d6cee4addb6401a22/versioned_docs/version-6.x/layout-api.md
[cssa]: https://github.com/single-spa/create-single-spa/blob/c32f9da2a06ae60c895ba4cd5571c52ec2a5d542/packages/generator-single-spa/src/root-config/templates/src/index.ejs
[imo-sec]: https://github.com/single-spa/import-map-overrides/blob/c0a09921e5d6c5c6dae7988e1af9890f36555caf/docs/security.md
[imi]: https://github.com/single-spa/import-map-injector/blob/26903341e0215247b044fa1a16b90ab9468aaef7/README.md
[sjs]: https://github.com/systemjs/systemjs/blob/9647576d43294e938ddae8fe231beb62255f4e46/README.md
[sjs-im]: https://github.com/systemjs/systemjs/blob/9647576d43294e938ddae8fe231beb62255f4e46/docs/import-maps.md
[sjs-mt]: https://github.com/systemjs/systemjs/blob/9647576d43294e938ddae8fe231beb62255f4e46/src/extras/module-types.js
[vpf]: https://github.com/originjs/vite-plugin-federation/blob/91014da9a39d9ea264e2ca7c5d22c620d5752073/README.md
[vpf-types]: https://github.com/originjs/vite-plugin-federation/blob/91014da9a39d9ea264e2ca7c5d22c620d5752073/packages/lib/types/index.d.ts
[vpf-expose]: https://github.com/originjs/vite-plugin-federation/blob/91014da9a39d9ea264e2ca7c5d22c620d5752073/packages/lib/src/prod/expose-production.ts
[vpf-remote]: https://github.com/originjs/vite-plugin-federation/blob/91014da9a39d9ea264e2ca7c5d22c620d5752073/packages/lib/src/prod/remote-production.ts
[vpf-utils]: https://github.com/originjs/vite-plugin-federation/blob/91014da9a39d9ea264e2ca7c5d22c620d5752073/packages/lib/src/utils/index.ts
