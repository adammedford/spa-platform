# ADR-0019: A gateway build accepts the registry `schemaVersion` it was built with, and refuses a value it cannot apply

- **Status:** Accepted
- **Date:** 2026-10-16
- **Deciders:** integrator (task I-7)
- **Plan ref:** No decision row. Gap MR-1 in `docs/roadmap.md`; ADR-0015 decision 4, which deferred
  the `timeoutSeconds` maximum until this rule existed; overnight Q26, accepted 2026-10-16. Kept
  narrow on purpose: no grill has tested MR-1 as a whole, and this decides only what ticket 49
  (task 2.1b) and the next gate need.

## Context

`registry.schema.json` pins `schemaVersion` to `const: 1` and says "a later shape gets a later
number, never a changed meaning". The gateway validates every document against that schema through
`Platform.Registry`, so a build already refuses any other number. Nothing wrote that down as the
build's rule, and nothing said whether refusing a value the build cannot honour is "a later shape".

That second question is the one blocking work. Task 4.1a measured the longest `timeoutSeconds` YARP
2.3.0 applies: 2,147,483 seconds (`ProxyTimeout.MaximumTimeoutSeconds`). The validator's own bound is
`TimeSpan.MaxValue` in seconds, about 430,000 times larger, so a document can validate, go live, and
have the proxy silently apply a different limit from the one it states. ADR-0015 decided the fix
(the contract states the bound, the validator refuses above it, the proxy proves it can apply it) and
deferred it to this rule.

## Decision

**1. A build accepts exactly the `schemaVersion` values its contract schema names.** Today that is
`1`. The schema's `const` is the rule, and the validator's refusal of any other value is its
enforcement; a build's release notes name the versions it accepts. A rolling deployment (stage S8)
may run two builds against one document only when both accept its `schemaVersion`.

**2. A later shape gets a later number.** A change that gives an existing document a different
meaning, or makes a document that a build applied as written stop validating, is a new
`schemaVersion`.

**3. Refusing a value no build could apply as written is not a later shape.** A `timeoutSeconds`
above 2,147,483 was never applied as written: the proxy applied something else. Refusing it changes
the meaning of no document that worked, so it stays within `schemaVersion: 1`. The schema gains
`"maximum": 2147483` on `timeoutSeconds`, its description says why, and the validator refuses above
it with the same issue shape as every other bound.

**4. The schema edit lands at G2.** It narrows a frozen schema, and `contracts/README.md` holds a
narrowed schema for the next gate. The integrator makes it in the commit that closes G2, before the
`g2-local-slice` tag; ticket 49 (task 2.1b) then replaces the validator's model-level bound with the
schema's, and workstream 4's ceiling test asserts that the proxy applies the contracted maximum, as
ADR-0015 decision 3 says.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| A gateway version that each registry document names | Introduces a second version beside `schemaVersion` with nothing yet to version; MR-1's wider question (what a build is called in a log line) stays open for S8 rather than being half-answered here. |
| Bump to `schemaVersion: 2` for the maximum | Every document in every bucket would have to change to say the same thing, for a refusal of values that never worked. |
| Keep ADR-0015's deferral | Leaves a known gap where the registry accepts a timeout the proxy cannot honour, with no condition left that would end it. |

## Consequences

- Ticket 49 (task 2.1b) unblocks once the schema edit lands at G2.
- MR-1's remaining half — a version a supporter can read in a log line, and the old-build/new-document
  test for rolling deployments — stays with S8 and is now the whole of MR-1.
