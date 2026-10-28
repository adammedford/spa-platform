# Test id register

The test ids named after the 11 October 2026 export, each with its tier and the decision that named
it, the tiers of the export's rows that carry none, and the checks still owed an id.

The export's own ids, F-01 to F-11, N-01 to N-27, P-01 to P-06 and A-01 to A-05, are listed in
[`docs/plan/04-tests.md`](../docs/plan/04-tests.md) and are not copied here. Evidence ids (`S-`,
`R-`, `T-`, `C-`) and rule ids are not test ids and are not listed here either.

## Why this file is here

Decided on #322. The export is frozen and cannot take a new row, so an id named after it needs a
live home, and one register under `contracts/` is the one place a session looks. The rejected
alternative was a convention that the ADR owning each check records its id, which is where P-07
first lived (ADR-0016 decision 7). This file never freezes.

**How an id is added.** The integrator adds it by landing a decided change request
(`docs/agents/tracker-policy.md` → "Change requests"), never the workstream that needs it. A row
lands between gates as prose, under [`README.md`](README.md)'s rule, except that a row whose check
waits for a gate (one needing a new rule id, say) lands in that gate's integrator commit.

**Numbering.** A new id continues its prefix's sequence: F-12, N-28, P-08 and A-06 are next. A number
is given only when the row's change request is decided, is never reused and never takes a letter
suffix. A dropped check keeps its row, marked retired, so old merge bodies, tags and review records
stay true.

**Row shape.** The export's columns for the prefix, plus Tier (`H` hermetic, `D` deployed) on every
table and a Recorded by column naming the ADR, change request or confirmed answer that named the id.

## Functional (F)

| ID | Test | Expect | Tier | Criterion | Recorded by |
| --- | --- | --- | --- | --- | --- |

None yet.

## Negative security (N)

| ID | Attempt | Expect | Tier | Recorded by |
| --- | --- | --- | --- | --- |

None yet.

## Infrastructure and architecture properties (P)

| ID | Check | Expect | Tier | Recorded by |
| --- | --- | --- | --- | --- |
| P-07 | Identity plan allowlist | One user pool, one domain, the resource servers and test users `fixtures/cognito/pool.json` lists with a generated password and a parameter per user, and no app client | H | ADR-0016 decision 7; Q45, 2026-10-15 |

P-07, like P-01 to P-03, is a mock-provider assertion in `terraform test`
(`infra/envs/poc/idp/tests/root.tftest.hcl`), run by CI's Terraform test step with no AWS
credential. This row is the one to edit from now on; ADR-0016 keeps decision 7 as written.

## Architecture reference rules (A)

| ID | Check | Expect | Tier | Recorded by |
| --- | --- | --- | --- | --- |

None yet.

## Tiers of the export's untiered rows

The export's P and A rows carry no Tier column; their tiers are in the paragraph under its
infrastructure table. This table gives them a row a reader can use. It is supplemental metadata,
not a second definition: an export id it tiers is not a duplicate, and its Check and Expect stay in
the export.

| Id | Tier | Recorded by |
| --- | --- | --- |
| P-01 to P-03 | H | `docs/plan/04-tests.md`, the paragraph under the infrastructure table |
| P-04 to P-06 | D | `docs/plan/04-tests.md`, the paragraph under the infrastructure table |
| A-01 to A-05 | H | `docs/plan/04-tests.md`, the paragraph under the infrastructure table |

## Owed

Checks a decided request names that are still owed an id. No number is reserved: the integrator
numbers, tiers and moves each into its table in the commit the request names.

| Check | Owed by | Numbered in |
| --- | --- | --- |
| Functional: a logout token ends the subject's sessions on its app | `changes/p2-6-pingfederate-profile.md`, item 7's decision | G7's integrator commit |
| Negative: each refused logout token is a 400 under `auth.backchannel`, including a token missing `iat`, one missing `jti` and one whose `iat` is in the future | `changes/p2-6-pingfederate-profile.md`, item 7's decision | G7's integrator commit |
