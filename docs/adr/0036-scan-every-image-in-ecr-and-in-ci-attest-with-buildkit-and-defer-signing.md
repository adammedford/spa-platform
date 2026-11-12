# ADR-0036: Scan every image in ECR and in CI, attest it with BuildKit, and defer signing until a verifier other than the signer exists

- **Status:** Accepted
- **Date:** 2026-11-12
- **Deciders:** integrator, on Adam's answers to #779, recorded in
  https://github.com/adammedford/spa-platform/issues/779#issuecomment-6767167834
- **Plan ref:** No decision row. Plan row P2-9 (`docs/plan/06-phase-2-and-risks.md`), whose proof
  reads "Scanner and policy checks pass"; the image half of roadmap gap MR-7 (`docs/roadmap.md`);
  spec #778 and its decision ticket #779. Amends no ADR.

## Context

P2-9's proof is "Scanner and policy checks pass". Roadmap gap MR-7 notes that P2-9 says so "without
naming a scanner, a threshold or an SBOM", and ADR-0035 decision 1 left supply-chain analysis, the
SBOM and the pin cadence among it, to MR-7. #779 put the image half to Adam as five questions, and a
sixth because #784 left the registry's settings to it.

When he answered, at `origin/main` f435190:

- The cell's two ECR repositories set immutable tags and no scan configuration
  (`infra/modules/cell/storage.tf`), and CI holds no AWS credentials (ADR-0016).
- Package advisories only warn (`AGENTS.md` → "Build and test"), and nothing checks an image.
- `tools/scripts/deploy.sh` builds with `--provenance=false --sbom=false`. Its comment gives the
  reason: with either, buildx pushes an image index whose digest is not the platform manifest's, and
  the pushed digest is the one asserted against the running task.
- `deploy.sh` builds, pushes and applies in one run under the operator's own session.
- The base images are pinned by digest (#781, #782), and `tools/pins_inventory.py` lists them as the
  report-only `base-images` family, which reads no registry.
- The state bucket is encrypted with SSE-S3, which holds no key to manage
  (`infra/bootstrap/state.tf`).

## Decision

1. **The scanner.** ECR basic scanning on push, plus Trivy in CI. Only the registry sees the
   deployed ARM64 digest, and only CI can fail a change before it merges.
2. **The threshold.** CI fails on a HIGH or CRITICAL finding that has a fix. An accepted finding is
   written in a tracked file with an issue and an expiry of 30 days. The 30 days follow .NET's
   monthly patch releases.
3. **The SBOM.** BuildKit's SPDX attestations, kept in ECR with the image. `deploy.sh`'s digest
   assertion is reworked for the image index the attestations bring (#783).
4. **Signing.** Deferred until a verifier other than the signer exists. A signature checked in the
   run that built, pushed and applied the image, under the same session, adds nothing to that run's
   digest assertion.
5. **The base-image pin cadence.** The pins sweep reads each pinned tag's current digest monthly and
   files an issue for each one that moved.
6. **ECR encryption.** AES256, the default, following the state bucket's SSE-S3.

Decisions 1 to 5 were each recorded as two-way. Decision 6 is one-way for each repository
(Consequences).

## Alternatives rejected

Each was offered on the decision sheet of 2026-11-09 and not chosen. The reasons given for *ECR
basic scanning only* and for decision 4's two options follow from the answers' own sources. The
other reasons were written with this ADR's ticket, #1164, and Adam approved that ticket's body there
before this ADR was committed.

- **Decision 1.** *ECR basic scanning only*: CI holds no AWS credentials, so nothing would fail a
  change before it merges. *ECR enhanced scanning (Amazon Inspector)*: it is billed for each image
  scanned (#779's body), and it is a registry setting for the whole account, outside the cell
  (ADR-0012) and outside the tag query that proves a teardown (P-05).
- **Decision 2.** *CRITICAL only*: a HIGH finding that has a fix would pass. *Findings with no fix
  fail too*: a merge would wait on a finding no change can fix.
- **Decision 3.** *An SBOM the scanner writes, attached as a referrer*: the image stays a single
  manifest, but the SBOM is one more step of the build, and provenance needs another source.
- **Decision 4.** *AWS Signer with Notation, if the project plan offers Signer*, and *cosign with a
  KMS key*: either signature would be made and checked in one run under one session, which is what
  decision 4 defers.
- **Decision 5.** *Dependabot*: its pull requests move a Dockerfile's pin without its row in
  `docs/dependencies.md`.
- **Decision 6.** No other option was offered. #784's body had named a customer-managed KMS key as
  the other value.

## Consequences

- #783 attaches the attestations and reworks `deploy.sh`'s digest assertion, and no longer signs.
  Signing is #1165, parked until a verifier other than the signer exists.
- #784 sets scanning to the basic level, and its lifecycle rule must not expire an attestation
  manifest.
- #785 adds the Trivy step and shows first that Trivy reads the package list of a chiseled image,
  since a scanner that reads nothing passes every image.
- #788 expects no signature.
- Decision 5 needs new sweep work, which is #1166: the `base-images` family reads no registry.
- A repository's encryption is set when it is created and cannot be changed afterwards (Evidence),
  so decision 6 is one-way for each repository: a move to a KMS key is a new repository.
- Three things were not checked when the decisions were made. That Trivy reads the package list of a
  chiseled image: #785 shows it before its scan step is relied on. That the attestation's format is
  SPDX, and that the digest ECS reports can be reconciled with an image index, which only a run
  against a cell shows: #788 records both, and if the digest cannot be reconciled, decision 3
  returns to Adam.

## Evidence

- Adam's answers, with the options offered and each answer's source:
  https://github.com/adammedford/spa-platform/issues/779#issuecomment-6767167834.
- `origin/main` at f435190: `infra/modules/cell/storage.tf`, `tools/scripts/deploy.sh`,
  `tools/pins_inventory.py`, `infra/bootstrap/state.tf`.
- Amazon ECR User Guide, "Encryption at rest", read on 2026-11-10: "Repository Encryption
  Configuration can't be changed after a repository is created."
  https://docs.aws.amazon.com/AmazonECR/latest/userguide/encryption-at-rest.html
