# ADR-0039: The unit of adoption is the gateway image, its contracts and spactl; the infrastructure is a reference

- **Status:** Proposed
- **Date:** 2026-11-18
- **Deciders:** Adam, on the three-modes map (#1317): "The unit of adoption: what an adopter takes, and whether by clone or as an artefact" (#1320), amended by "The configuration reference…" (#1325); #1335.
- **Plan ref:** No decision row.

## Context

`AGENTS.md`'s first rule says the gateway is the product and asks of every component whether an adopter, a team running the gateway in its own estate, would reuse it. Nothing said what an adopter takes. The repository holds a working cell module, operator scripts and runbooks beside the gateway, and all of them deploy this project's proof cell. The module always creates its own VPC, with no existing-VPC input, and reads the contracts by relative path. No workflow publishes an image or a release asset, and the Dockerfile needs the repository root as its build context. Checks A-01 to A-04 already keep the image free of fixtures.

## Decision

1. **The unit of adoption is three artefacts built from one ref, taken as they stand**: the gateway image built from `src/Gateway/Dockerfile`; the contracts that govern it; and `spactl`. The contracts in the unit are the ones the list in `contracts/README.md` names: the platform configuration, registry, app-definition and outputs schemas, `http-contract.md`, `upstream-contract.md`, `s3-layout.md`, `telemetry.md` and `runtime-contract.md`.
2. **The cell module, the operator scripts, the runbooks and the browser client are references**: an adopter may copy and change them, and nothing promises they stay as they are. The module is not for sourcing, and its variables are not a versioned interface.
3. **Everything else is not taken**: the poc roots, the bootstrap root, the Cognito module, the fixtures, the tests and the rest of `tools/`. The composed suites stay runnable from a clone, with no promise.
4. **The adopter designed for** is an enterprise team with its own VPC, load balancer conventions, identity provider, image registry and Terraform standards.
5. **Until publishing exists, the adopter builds the unit from an unmodified clone**, never from a fork it works inside.
6. **What the image needs from where it runs is a promise**, stated exhaustively in `contracts/runtime-contract.md`, so that the written requirement, the one part of the module every adopter reuses, is in the unit.
7. **There is no second configuration of the image for trying it without AWS.** The way to see the gateway work without AWS is the hermetic cell, from a clone.

## Alternatives rejected

- **The cell module as a sourced module.** The adopter in decision 4 cannot apply a module that creates its own VPC, and promising its variables makes the proof cell's shape the product.
- **Adoption by fork.** A fork makes every path something an adopter may depend on.
- **The scripts or the runbooks taken as they stand.** A script cannot be more stable than the infrastructure it drives.
- **The outputs schema outside the unit.** `spactl --outputs` checks its input against it, and a part of the unit should not read a shape outside it.
- **`pipeline.md`, `request-gate.md` and `resource-tags.md` in the unit.** They describe how this repository is built and tagged, not what an adopter configures.
- **An AWS stand-in or a filesystem registry in `src/Gateway`.** A configuration nobody runs in production proves nothing an adopter reuses.
- **Leaving the estate requirement in the module's README.** It would be readable only from a part the adopter does not take as it stands.

## Consequences

- A change to a contract in the unit, or a new AWS call in the image, is a change request: a promise once made is kept.
- A reference names nothing of the proof cell, and a check holds that.
- The proof cell walks the reference scripts and runbooks, which is what keeps them true.
- The repository needs a licence before anyone may take the unit.
- Three triggers would change this decision. A real adopter asks to source the module: it gains an existing-VPC input and becomes a promise. Publishing arrives: the build-from-clone step drops, and the unit is unchanged. An adopter off ECS or off AWS: the image stays the unit, and the S3 and Parameter Store coupling becomes an entry in `docs/gaps.md`.

## Evidence

- #1320's resolution and #1325's resolution, both 2026-11-17, read on trunk at `df4cf96`.
- The map, #1317, and #1335.
