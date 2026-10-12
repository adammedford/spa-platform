# Upstream contract

- **Freezes at:** G1a
- **Consumed by:** workstream 4 and the mock API
- **Source:** `docs/plan/02-contracts.md` → Upstream contract

What an API behind the gateway receives, and what it must check for itself. An upstream is an origin
the registry names under `apis[].upstream`, bounded by `Policy:AllowedUpstreams`. Not "backend" and
not "service".

| Direction | Rule |
| --- | --- |
| Sent upstream | Method, query and body unchanged. Path with `/api/{name}` removed. `Authorization: Bearer` with the session's access token. `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` set by the gateway. `traceparent`. |
| Never sent upstream | `Cookie`. Any `Authorization` the browser sent. `X-CSRF-TOKEN`. Forwarding headers the browser supplied: `Forwarded`, `Via`, `X-Real-IP`, and every `X-Forwarded-*` and `X-Original-*`. A list of names is never complete, so an API trusts only the three headers the gateway sets. |
| Checked by the API | Signature against the issuer's keys. `iss`. `exp`. For Cognito, `token_use` is `access`. `client_id` is on the API's own allowlist. The scope the route needs. `aud` equals the API's identifier when resource binding is on. |
| Returned to the browser | Status, headers and body from the upstream, minus `Set-Cookie` and `Access-Control-*`, whatever the upstream's status. The gateway's security headers replace the upstream's. Redirects are passed through, never followed. |

The gateway is not the only check. Assumption A5 is that upstream APIs accept a bearer token and
validate it themselves, and criterion 7 rules out the gateway being the only one that does.
Admission is the API owner's decision: F-07 onboards app 3 and the mock API refuses its call,
because it has not admitted that client.

## One token per app

Phase 1 gives each app one access token, so one audience. An app with several upstreams sends the
same token to each. That is safe only when those upstreams share a trust domain, and the validator
warns when an entry lists more than one upstream origin. The warning is not expressible in
`registry.schema.json`, which has no notion of a warning; task 2.1 raises it and keeps it apart from
the errors.

## Timeouts and failures

| Case | Answer to the browser |
| --- | --- |
| The upstream could not be reached | 502 `upstream_unavailable` |
| The upstream exceeded the entry's `timeoutSeconds` | 504 `upstream_timeout` |
| The app lists no upstream under that name | 404 `unknown_api`, and no upstream sees the request |
