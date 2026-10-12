# ADR-0010: Serve every host from one wildcard listener with no listener rules

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D10 (Doc revision 36)

## Context

Phase 1 acceptance criterion 6 requires a third SPA to be onboarded with no new runtime
infrastructure and no gateway restart. Ingress is the usual place that promise breaks: a new host
normally means a new DNS record, a certificate change or a listener rule.

## Decision

Wildcard DNS and a wildcard certificate on one listener, forwarding to a single target group with
the `Host` header preserved. No listener rules. Host resolution happens in the gateway: the request
authority must equal a registry `host` exactly, and any other host gets 404 before routing runs.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Per-app listener rules, DNS records or certificates | Onboarding would touch ingress every time, which criterion 6 forbids. |

## Consequences

- Onboarding never touches ingress.
- Assumption A1 is load-bearing: SPA hosts are single-label names under one internal zone per cell, so one wildcard certificate and one wildcard record cover them. If that is wrong, onboarding starts touching certificates or DNS.
- Host confusion is answered in one place by exact authority match. `X-Forwarded-Host` is never read (T4, N-08).
- `X-Forwarded-Proto` and `X-Forwarded-For` are trusted from the ALB subnets only, with a forward limit of one, and the task security group admits only the ALB (T6, N-09, P-06).
- Onboarding runs in a separate Terraform root with a resource-type allowlist, so it cannot create a runtime resource (P-02).

## Evidence

Tests F-07, N-08, N-09, P-02, P-06. Topology in `docs/plan/05-infrastructure-and-cost.md`.
