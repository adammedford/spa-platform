# ADR-0003: Reference client secrets in SSM Parameter Store by name and version

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D3 (Doc revision 36)

## Context

Each app has one confidential OAuth client, so the gateway needs a client secret per app. The
registry is a configuration document that platform administrators write, and it is kept in S3 with
every version retained.

## Decision

Secrets live in SSM Parameter Store. A registry entry names one by parameter and version
(`clientSecretRef: "ssm:/spa-platform/poc/apps/app1/client-secret:1"`), and every reference is
resolved before a snapshot goes live.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Secrets in the registry | Would put credential material in a versioned S3 object with every historical value retained. |
| AWS Secrets Manager | Paid per secret per month for a capability standard parameters give at no cost here. |

## Consequences

- Standard parameters cost nothing.
- A versioned reference makes rotation a registry change rather than a deployment.
- A snapshot with a missing or unreadable secret never replaces a working one.
- Secrets reach logs by reference only, never by value (see ADR-0007's logging rules and test N-16).
- The task role scopes Parameter Store access to this cell's prefix (P-03).

## Evidence

Real-handler run R-C2 (a secret rotated at runtime). Rotation procedure in
`docs/plan/05-infrastructure-and-cost.md` → Rotating a client secret.
