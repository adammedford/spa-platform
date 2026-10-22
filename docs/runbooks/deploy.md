# Deploy the gateway

Put a new build of the gateway and the mock API into the cell. The Gateway lever of
`docs/plan/05-infrastructure-and-cost.md`, Deployment and rollback: "`deploy.sh` builds, pushes,
applies the new digest and checks that the running task has it". Task 6.9 exercises this note.

**It signs every user of every app out** (Phase 1 sessions live in the task's memory). If the change
is a SPA release, a registry entry, a client or a secret, use that lever instead: none of them needs
a deployment.

Start with the operator session in [README.md](README.md). Docker Desktop must be running, with
buildx (decision D-2 in `docs/dependencies.md`).

## A deployment to a running cell

```bash
tools/scripts/deploy.sh
```

That is the whole sequence. In order, and it stops at the first that fails (its header has the
detail):

1. It reads the live registry the gateway polls and refuses when there is none or it holds no app,
   because a gateway started against an empty registry never becomes ready and the deployment would
   fail its health checks. Nothing is built before this.
2. It builds both images for ARM64 and pushes each under a tag of its own (the repositories' tags are
   immutable), then takes each digest from ECR.
3. It plans and applies `infra/envs/poc/platform` with both digests and the health check on
   `/readyz`, and records them in `images.auto.tfvars` there, so every later apply of that root
   passes the same digests back and never moves the image (plan row 1.7).
4. It rewrites `infra/envs/poc/outputs.json` from the three roots.
5. It waits for a stable service, then asserts that the running gateway is the digest it pushed and
   that the task's target is healthy on `/readyz`.

Exit 0 means the new gateway runs steady and healthy. Exit 1 names the step that failed; exit 2
means it did not start (a missing tool, no Region, no credentials, no outputs document).

Then check it from the workstation. The tunnel's hop is the gateway task, which the deployment
replaced, so a tunnel opened before it has ended: start `tools/scripts/tunnel.sh` again, then

```bash
tools/scripts/verify-tunnel.sh /healthz
```

Every user signs in again on their next request.

## If it fails

- **Before the apply** (steps 1 and 2): nothing the cell runs changed (step 2 may have pushed an
  image to ECR, under a tag of its own, which nothing uses). Fix the cause and run it again.
  A step-1 refusal means the registry is empty: run `tools/scripts/seed.sh`.
- **The apply or after it** (steps 3 to 5): the deployment circuit breaker rolls a deployment whose
  tasks fail their health checks back to the previous task definition on its own
  (`docs/plan/05-infrastructure-and-cost.md`). If the service is steady but on the wrong gateway,
  or you want the previous one back, use [rollback.md](rollback.md).

## The first deployment of a new cell

The order matters once, because each step feeds the next (`docs/plan/05-infrastructure-and-cost.md`,
First deployment):

1. `tools/scripts/certs.sh`, then the bootstrap root (`infra/bootstrap/README.md`).
2. The identity root, then the apps root: `terraform -chdir=infra/envs/poc/idp apply`, then
   `terraform -chdir=infra/envs/poc/apps apply`. Cognito now has the two clients.
3. The platform root on the sample image: `terraform -chdir=infra/envs/poc/platform apply`, whose
   health check is `/` for the sample.
4. `tools/scripts/seed.sh` publishes the fixture releases and writes the first registry through
   `spactl`. `deploy.sh` refuses to run before it.
5. `tools/scripts/deploy.sh`, as above. The target turns healthy only once `/readyz` reports a
   snapshot. While the service settles, the sample task's target shows as draining and fails the new
   `/readyz` check; `deploy.sh` tolerates that (#207).
6. `tools/scripts/tunnel.sh`, then `tools/scripts/verify-tunnel.sh /healthz`.
