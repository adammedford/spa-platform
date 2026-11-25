# Widen or narrow the bounds

Change a cell's platform configuration so that a registry entry may name an upstream or an issuer
the cell does not admit yet, or so that a shell may list more fragments than the cell allows.
Onboarding stays inside the bounds and needs no restart: an app that needs more than they allow
needs this note first, and onboarding starts once it is done.

**Widening is a deployment, never a registry write.** The bounds are settings in the gateway task's
environment. The cell module, `infra/modules/cell`, sets `Policy__*` and `Issuers__*` on the task
definition (`ecs.tf`), and the gateway reads them at start-up. Each is a security control that a
registry writer cannot change (`GLOSSARY.md` → "Platform configuration"), so `spactl` refuses an
entry outside them before it writes anything, and its refusal says the entry is outside the cell's
bounds.

**It signs out whom a deployment signs out.** That depends on the cell's shape, and
[deploy.md](deploy.md) states it.

Start with the operator session in [README.md](README.md).

## What it covers

| An app or shell that needs | Platform configuration | The cell module's input |
| --- | --- | --- |
| A new upstream | `Policy:AllowedUpstreams` | `allowed_upstreams` |
| A new issuer | `Issuers:{name}`, its `Authority`, `Profile` and `BrowserOrigin` | `issuers` |
| More fragments in one shell | `Policy:FragmentsPerShell` | `fragments_per_shell` |

The platform root, `$SPA_PLATFORM_ROOT`, sets these inputs; `contracts/platform-config.schema.json`
is the shape of what they become. A fragment's client belongs to its shell's issuer, so a fragment
never needs a new issuer, but a fragment that calls a new upstream, or one past the cap, needs this
note as an app does.

**A new issuer is checked before it goes in**, against the issuer section of the identity provider
contract, `contracts/identity-provider-contract.md` → "The issuer": its discovery document, its
signing keys, its access and ID tokens, its revocation endpoint and how it ends a session under its
profile. The gateway trusts every issuer in the configuration to sign users in, and nothing in
this note checks the issuer for you.

## A new host suffix is not a widening

A host outside the cell's suffixes needs DNS and a certificate in a zone the cell does not have.
Under ADR-0010's assumption A1, an app's host is a single-label name under the cell's one zone,
behind one wildcard certificate and one wildcard record, so a new suffix is a change to the cell
itself, not to its platform configuration. This note has no procedure for it.

## The order

1. **Apply the changed task definition.** Change the input in the platform root, then deploy as
   [deploy.md](deploy.md) has it, from the commit of the gateway the cell already runs, so that
   the deployment carries no change to the gateway's code beside the bounds. Its apply writes the new task definition.
2. **Wait until every task runs it.** `deploy.sh`'s exit 0 is that wait: the primary deployment
   completed on the applied task definition, and every running task is healthy on it. Until then,
   a task on the old task definition still holds the old bounds. It rejects a registry document
   with an entry outside them whole and keeps serving its last known good snapshot (ADR-0002), so
   it would hold every later registry change and `spactl wait` would time out.
3. **Rebuild `spactl`'s `--config` from that task definition**, as the README's "`spactl` and its
   platform configuration" builds it. A `--config` made before the deployment carries the old
   bounds, and `spactl` would refuse the entry the cell now admits.
4. **Then run `spactl app validate` and onboarding**, with the rebuilt `$CONFIG`. `app validate`
   checks the app's definition against the new bounds offline, before any work at the identity
   provider.

## Narrowing the bounds

Narrowing is this note run in reverse, done only on purpose: an upstream, an issuer or fragment
capacity the cell should no longer admit. First take out, or change, every registry entry that
relies on what goes, and wait until every instance reports that revision. Then change the input in
the platform root and deploy, wait until every task runs the new task definition, and rebuild
`--config` from it.

The registry goes first because a gateway validates the document against the bounds it started
with. A task that starts on the narrowed task definition rejects a document that still holds an
entry outside them whole. A new task has no last known good snapshot to keep, so it never becomes
ready, and the deployment fails its health checks and rolls back ([deploy.md](deploy.md), "If it
fails").

Narrowing is never a step of removal. Removing an app or a fragment is a registry change and stays
restart-free; an upstream or an issuer no entry names any more stays in the bounds until an operator
narrows them on purpose.
