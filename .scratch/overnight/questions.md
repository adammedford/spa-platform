# Questions for Adam, overnight 2026-10-13

Every question from the tickets' "## Questions for Adam" sections (no ticket has a "## Comments" section). Numbered continuously. A `Qn` or "question n" inside a question's own text is that ticket's internal numbering; the file's numbers are the `### Q` headings. Ordered so the questions that unblock the most work come first. Each recommendation was adopted provisionally during the paper grilling; nothing is decided until you answer. Write `yes`, `no` or your own answer after "Answer: yes".

## Ticket 17: 3.0, G0: the Cognito items

File: `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/issues/17-3.0-g0-cognito.md`

_Unblocks the most: 3.0 is the head of the critical path (3.0, then I-6, then 3.1 to 3.9 and 4.2)._

### Q1

Should the Cognito harness go in a new sibling folder, `spikes/g0/cognito/`, built from the hardened and token-and-proxy sources, with `output-cognito.txt` committed?

Recommended: Yes. Why: the passed runs stay untouched, and the run reuses the exact code that passed.

Answer: yes

### Q2

Should Playwright Chromium (1.63.0, pinned in the spike) drive managed login, with a `--manual` fallback?

Recommended: Yes. Why: managed login is JavaScript-rendered, and `Browser.cs` is only an `HttpClient`.

Answer: yes

### Q3

Should the harness read the infra outputs document and `fixtures/apps/*.json` for its configuration?

Recommended: Yes. Why: it then runs the same clients and issuer the gateway will use, and it tests the outputs contract early.

Answer: yes

### Q4

Should secrets be read from Parameter Store at run time, with only lengths, hashes and non-credential claims printed, and a self-check that fails the run if a credential appears in the output?

Recommended: Yes. Why: the output is committed, and redacting by hand misses things.

Answer: yes

### Q5

Should the gate path use the token library as task 3.8 configures it, and the item 2 questions use raw calls through `Duende.IdentityModel`?

Recommended: Yes. Why: only the library path proves tasks 3.8 and 3.9, and only raw calls can choose which refresh token is sent.

Answer: yes

### Q6

Should this run's evidence ids use a `C-` prefix numbered per item (`C-2.1`), listed in the spike README?

Recommended: Yes. Why: `07-evidence.md` is frozen, and item numbering avoids a clash with `T-C`.

Answer: yes

### Q7

Should 3.0 report through a "Gate G0, as run" section in `spikes/g0/README.md`, with a row per G0 item and per item 2 question?

Recommended: Yes. Why: `docs/` is the integrator's, and a change request is for asking for a change, not for reporting.

Answer: yes

### Q8

Should items 1, 4 and 5 be cited from the 11 October runs rather than run again?

Recommended: Yes. Why: they passed on the shipped packages, and the Cognito run exercises item 4's configuration anyway.

Answer: yes

### Q9

Should "Blocked by" name 1.1 applied, 1.8 applied, `certs.sh` run, the hosts-file line, port 8443 free and AWS credentials?

Recommended: Yes. Why: the ticket names only 1.8, and 1.6a landed written but not run.

Answer: yes

### Q10

Should the ticket split into 3.0a, where an agent writes the harness and dry-runs it now, and 3.0b, where Adam runs it against Cognito?

Recommended: Yes. Why: your time in AWS becomes one run instead of a debugging session.

Answer: yes

### Q11

Should the grace window count as insufficient, which reopens ADR-0005's reserve design, if a replay inside the window is refused or a late replay kills the successor token?

Recommended: Yes. Why: the plan uses the word without defining it, and these are the two outcomes that break task 3.8.

Answer: yes

### Q12

If revoking a rotated-out token leaves its successor alive, should task 3.9 revoke the token re-read from the session record, and the T-B18 race stay a documented residual?

Recommended: Yes. Why: the successor is held only on the server and its session record is deleted.

Answer: yes

### Q13

If Cognito's refresh tokens are longer than 4,096 characters, should the route be item 4's way out, an in-house refresh class over `Duende.IdentityModel`?

Recommended: Yes. Why: the plan gives no way out for this, and that one is already planned and small.

Answer: yes

### Q14

If `aud` is lost on a refresh without `resource`, should task 3.8 send `resource` on every refresh and accept T-B12's extra grant?

Recommended: Yes. Why: keeping the audience is worth one more call per login.

Answer: yes

### Q15

If resource binding does not set `aud` at all, should that be a finding (APIs check `client_id` and scope, as the mock API's allowlist already does) rather than a G0 failure?

Recommended: Yes. Why: the audience check is defence in depth, and the plan has no way out for this case.

Answer: yes

### Q16

If discovery lists no revocation endpoint, should the `cognito` profile supply `{BrowserOrigin}/oauth2/revoke` through a new `IIdpProfile` member that I-6 adds?

Recommended: Yes. Why: otherwise the library's revoke call throws (T-B14), and the interface is still draft.

Answer: yes

### Q17

Should G0 try both client credential styles and keep `PostBody` if both work?

Recommended: Yes. Why: T-B4 and T-B5 hand this to item 2, and the code exchange and the refresh must agree.

Answer: yes

### Q18

Should item 3 pass only if Cognito accepts the `:8443` callback and sign-out URLs and managed login works with the Terraform-made branding style, for both apps?

Recommended: Yes. Why: those are the two halves of item 3 as the plan states it.

Answer: yes

### Q19

Should G0 add an expectation that logging out of app1 at Cognito leaves app2's session and refresh working?

Recommended: Yes. Why: Cognito's logout is global, and the per-app boundary has never been checked against it.

Answer: yes

### Q20

Should refreshes be forced with `ForceTokenRenewal`, with one natural 5-minute expiry on app1?

Recommended: Yes. Why: each run stays short, and one natural expiry still proves the timing.

Answer: yes

### Q21

Should item 2 fail only when login, callback, refresh or logout does not complete for a client, with the item 2 questions reported as findings with routes?

Recommended: Yes. Why: otherwise any surprising answer would stop all identity work.

Answer: yes

### Q22

If item 3 fails, should 3.0b record it and open tickets for workstreams 1 and 5, plus a change request for the contract examples, rather than take the way out itself?

Recommended: Yes. Why: workstream 3 owns none of the paths those ways out touch.

Answer: yes

### Q23

Should the harness create no AWS resources, and the pool and OAuth clients stay applied after G0 for G3?

Recommended: Yes. Why: 1.8's clients are the ones under test, and G3 needs them.

Answer: yes

## Ticket 01: I-6, Close G1b: fold G0 findings in and tag contracts-v1

File: `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/issues/01-I-6-close-g1b.md`

_Unblocks 3.1 to 3.9 and 4.2 once 3.0 reports._

### Q24

Should task 3.0 report G0 as a section in `spikes/g0/README.md` plus a recorded output file, one line per gate item and sub-question?

Recommended: Yes. Why: it follows the items 1, 4 and 5 pattern, so the evidence stays with its harness.

Answer: yes

### Q25

Does G1b wait for all five G0 items, item 3 included?

Recommended: Yes. Why: item 3's way out changes every `Host` value, so freezing before it risks an immediate change.

Answer: yes

### Q26

Does only a failure of item 2's core block the tag, with each sub-question answered by a response agreed in advance (Q13 to Q17 in the ticket, Q36 to Q40 here)?

Recommended: Yes. Why: a surprising answer then triggers an agreed response instead of a new meeting.

Answer: yes

### Q27

Are `AccessTokenResult` and `ISessionStore` part of the G1b draft set, changeable by I-6 without a change request?

Recommended: Yes. Why: one is `IAccessTokenSource`'s return type, the other the only path to `SessionRecord`, and their doc comments already say so.

Answer: yes

### Q28

Is the decision record always a new ADR-0014, "Freeze the identity contracts on the G0 report", plus status lines on ADR-0001, 0005 and 0011?

Recommended: Yes. Why: the freeze is hard to reverse, because scheme names set data-protection purposes. ADR-0013 exists, so the plan's "first decision record" is stale.

Answer: yes

### Q29

Does the `Profile` enum keep both `standard` and `cognito`?

Recommended: Yes. Why: the test identity provider runs the standard style today, and PingFederate needs it later.

Answer: yes

### Q30

Does `GET /bff/session` freeze `name` and `email` as nullable, and `expiresAt` as the session's end (`SessionRecord.ExpiresAt`)?

Recommended: Yes. Why: no test app asks for `email`, and the access token's expiry is internal.

Answer: yes

### Q31

Does `POST /bff/logout` become "attempts to revoke the refresh token, always removes the session"?

Recommended: Yes. Why: T-B14 already makes 3.9 do that, and it is the promise a browser can rely on.

Answer: yes

### Q32

Does the sentinel scheme stay private to `Platform.Identity`, out of `SchemeNames`?

Recommended: Yes. Why: no other workstream names it.

Answer: yes

### Q33

Is "G0 report" the term for the gate's verdicts and answers, leaving "finding" to the Evidence tab's sense?

Recommended: Yes. Why: in the Evidence tab "finding" means a probe-confirmed behaviour with an id.

Answer: yes

### Q34

At the freeze, do the schemas flip to `frozen-at-g1b` (dropping the root `exceptions`), do the `[Freeze("G1b")]` markers go while `FreezeAttribute` stays, and are the three freeze tests rewritten to assert an empty draft set?

Recommended: Yes. Why: it mirrors G1a's shape and keeps the mechanism for Phase 2.

Answer: yes

### Q35

Is `contracts-v1` an annotated tag on I-6's `main` merge, naming G1b, ADR-0014 and the green CI run?

Recommended: Yes. Why: that is the `skeleton-v1` convention, and gate state is read from tags.

Answer: yes

### Q36

If Cognito's grace window does not cover a replayed refresh token, does task 3.8 adopt the reserve design behind an unchanged `IAccessTokenSource`?

Recommended: Yes. Why: ADR-0005 already names this trigger, and the interface hides the swap.

Answer: yes

### Q37

If a Cognito refresh token is longer than 4,096 characters, is item 4's way out taken (one in-house class over Duende.IdentityModel) and ADR-0005 superseded?

Recommended: Yes. Why: it is the plan's own fallback, behind the same interface.

Answer: yes

### Q38

If `aud` is lost on a refresh without `resource`, does 3.8 send `resource` on every refresh, and if Cognito refuses that, is resource binding declared unsupported for the `cognito` profile with `oauth.resource` kept in the schema?

Recommended: Yes. Why: upstreams already check `client_id` and scope, and app2 shows that an entry without `resource` is valid.

Answer: yes

### Q39

If Cognito's discovery lists no revocation endpoint, does `IIdpProfile` gain an explicit member that supplies one, frozen at G1b?

Recommended: Yes. Why: T-B14 shows the library's revoke throws without one, and adding the member after the freeze needs a change request.

Answer: yes

### Q40

If revoking a refresh token does not revoke its successor, does 3.9 revoke the token it reads after any in-flight refresh, with no contract change beyond question 8?

Recommended: Yes. Why: the browser-facing promise is already the session's removal, and T-B18's race is 3.9's to handle.

Answer: yes

### Q41

If Cognito refuses a callback URL with a port, does I-6 bind 443, drop `:8443` from the platform-config example, and open follow-up tickets for 5.5 and 1.8?

Recommended: Yes. Why: that is the plan's way out, and `fixtures/` and `infra/` are not I-6's paths.

Answer: yes

### Q42

If item 2 fails with no fix inside 3.0, does I-6 stay open and untagged, with a new ticket comparing Finbuckle, Duende BFF v4 and a tenant-aware stack?

Recommended: Yes. Why: freezing on a failed mechanism would let 3.1 to 3.9 start on it.

Answer: yes

### Q43

Does I-6 also fix the stale ADR counts (`docs/agents/domain.md`, `CLAUDE.md`, `docs/adr/README.md`) and `AGENTS.md:90`?

Recommended: Yes. Why: these are integrator paths I-6 touches anyway, and stale state misleads later sessions.

Answer: yes

## Ticket 43: CR-5.5, Change request 5.5: TestApps no longer mirrors app1.json

File: `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/issues/43-CR-5.5-testapps-mirror.md`

_Unblocks nothing else; it closes task 5.5._

### Q44

Keep `TestApps`' defaults and drop the mirror claim? That means no change to `HostSuffix`, `Apis` or `issuer`, and no change to the fixtures.

Recommended: Yes. Why: N-17 needs app 1's second upstream, the port and issuer come from platform configuration, and equalising the double would break the Proxy tests that rely on empty `Apis`.

Answer: yes

### Q45

Add `Fixtures.Tests` to the `JsonSchema.Net` row in `docs/dependencies.md`?

Recommended: Yes. Why: it references the package today, for task 5.5's schema validation.

Answer: yes

### Q46

Close the request as I-7 was closed? That means Status `decided, <date>`, a `## Decision` section headed "Integrator, <date>.", and both edits applied in the same commit.

Recommended: Yes. Why: it follows the precedent, and prose-only edits to frozen files need no gate.

Answer: yes

### Q47

Have the new comment say `Entry` builds a "registry entry", not an app definition?

Recommended: Yes. Why: the glossary defines an app definition as an entry without `clientId`, `clientSecretRef` and `release`, and `Entry` fills all three.

Answer: yes

### Q48

Replace the sentence with wording that describes only the double and points at `fixtures/apps/`, without listing what `app1.json` contains? The suggested wording: "The defaults build the smallest registry entry the record accepts, not one platform policy would necessarily admit, not a copy of any fixture: no upstream, no CSP additions, an eight-hour session. Pass only what the test cares about. Real app definitions live under `fixtures/apps/`."

Recommended: Yes. Why: the request's proposed sentence restates fixture facts and would go stale the same way.

Answer: yes

### Q49

Keep `issuer = "primary"` until I-6, and revisit it when composed suites pair `TestApps` with real platform configuration?

Recommended: Yes. Why: nothing builds a `PlatformPolicy` yet, and `OAuthDefinition` is draft until G1b.

Answer: yes

### Q50

Keep `HostSuffix` without the `:8443` port?

Recommended: Yes. Why: the schema accepts a port-less host, only the unlanded 2.1 policy check would refuse it, and 2.1's tests set their own suffixes.

Answer: yes

### Q51

Have the Decision say the mirror was never true rather than that it drifted?

Recommended: Yes. Why: the config example (I-2) and the empty `Apis` default (I-3) contradicted the comment from its first commit, and the fix is to stop making the claim, not to sync it.

Answer: yes

### Q52

Also add `Fixtures.Tests` to the `Microsoft.Extensions.TimeProvider.Testing` row, and leave `spactl` in the `JsonSchema.Net` row for task 2.1 to settle?

Recommended: Yes. Why: it is the same staleness in the same file, and the column is "Expected in", so `spactl` is a planned user, not an error.

Answer: yes

### Q53

Add no new `CODING_STANDARDS.md` rule?

Recommended: Yes. Why: "A guard reads the thing it guards" already covers the case.

Answer: yes

### Q54

Leave the frozen `AppDefinition` record's name alone, and handle the clash with the glossary term only in comments and the glossary?

Recommended: Yes. Why: a rename after G1a touches every workstream to fix a naming nuance.

Answer: yes

### Q55

Confirm that workstream 5 has nothing further to do, so task 5.5 is done once this request is decided?

Recommended: Yes. Why: both items are integrator edits, and an open change request is the only thing keeping 5.5 from done.

Answer: yes
