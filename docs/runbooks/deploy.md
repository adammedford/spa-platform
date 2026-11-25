# Deploy the gateway

Put a new build of the gateway into a running cell: `deploy.sh` builds the gateway's image, pushes
it, applies its digest and checks that the running task has it. It builds, pushes and applies the
gateway image only. An image the cell runs beside the gateway is that cell's to build, and the apply
leaves it at what the platform root's tfvars hold.

**Whom it signs out depends on the cell's shape**, which the cell module's `multi_task` and
`session_store` inputs choose (`infra/modules/cell/variables.tf`):

- **One task with sessions in memory** (`multi_task = false`, `session_store = "memory"`, the
  defaults): it signs every user of every app out. Sessions and data-protection keys live in the one
  task's memory, and the deployment replaces that task.
- **Two tasks with the durable session store** (`multi_task = true`, `session_store = "dynamodb"`):
  a rolling deployment signs no one out. Sessions live in DynamoDB under the shared key ring, so a
  task of either build reads them (`g6-continuity`).
- **One task with the durable session store** (`multi_task = false`, `session_store = "dynamodb"`),
  which the module also accepts: by the same mechanism no one is signed out, since the new task
  starts and reads the stored sessions before the old one stops (`deployment_minimum_healthy_percent`
  100 in `infra/modules/cell/ecs.tf`). No gate has proved this shape.

If the change is a SPA release, a registry entry, a client or a secret, use that lever instead: none
of them needs a deployment.

Start with the operator session in [README.md](README.md). Docker Desktop must be running, with
buildx, and unless `--url` is given the readiness wait's tunnel needs the Session Manager plugin
installed and the cell's bastion: its hop is the bastion's task, never a gateway task, and the
platform root declares that task only with `bastion = true` (README.md, "Waiting for the gateway:
the tunnel").

Docker Desktop must also use the containerd image store. Step 2 builds the image with
`--sbom=true --provenance=mode=max`, and those attestations make the pushed tag an image index.
The classic image store cannot hold an image index, so with it Docker's default builder, the docker
driver, fails the build and nothing is pushed (`tools/scripts/deploy.sh`'s header;
https://docs.docker.com/build/metadata/attestations/). Turn it on before you run `deploy.sh`:
Docker Desktop, Settings, General, **Use containerd for pulling and storing images**, then Apply
(https://docs.docker.com/desktop/features/containerd/). Images in the classic store are hidden
after the switch, which costs nothing here because each build pulls its base again (`--pull`). To
check, run `docker info --format '{{.DriverStatus}}'`: with the containerd store it prints
`[[driver-type io.containerd.snapshotter.v1]]`, and output without that `driver-type` line means
the classic store (https://docs.docker.com/engine/storage/containerd/).

## A deployment to a running cell

```bash
tools/scripts/deploy.sh
```

That is the whole sequence: `deploy.sh` alone deploys and waits, and no tunnel need be running
before it or while it runs. In order, and it stops at the first that fails (its header has the
detail):

1. It reads the live registry the gateway polls and refuses when there is none or it holds no app,
   because a gateway started against an empty registry never becomes ready and the deployment would
   fail its health checks. Nothing is built before this. It keeps the registry's revision for
   step 5.
2. It builds the gateway's image for ARM64 and pushes it under a tag of its own (the repositories'
   tags are immutable), then takes its digest from ECR. It builds no other image.
3. It plans and applies the platform root, `$SPA_PLATFORM_ROOT`, with the gateway's digest and the
   health check on `/readyz`, and records them in `images.auto.tfvars` there, so every later apply
   of that root passes the same digest back and never moves the image. It passes no other
   container's image.
4. It rewrites the outputs document, `$SPA_OUTPUTS_FILE`, from the roots on `$SPA_ROOTS`, in order,
   through the field map `$SPA_FIELD_MAP`.
5. It waits for a stable service, then asserts that the primary deployment completed on the applied
   task definition, that every running task's gateway is the digest it pushed, that each running
   task's own target is healthy on `/readyz` and every other target healthy or draining, and that
   the outputs document names the applied gateway. Then it waits until each running task has
   answered `/readyz` at the revision step 1 read or later: one answer through the load balancer
   speaks for one task only, so a task the load balancer never picks fails the wait. The load
   balancer is internal, so this wait opens a tunnel of its own, `tools/scripts/tunnel.sh` through
   the cell's bastion task, on a free ephemeral local port the system picks, and closes it when the
   wait ends, pass, fail or Ctrl-C. An operator's tunnel on 8443 is left alone.

`tools/scripts/deploy.sh --url <origin>` names an origin this workstation already reaches for the
wait to read through instead, and then `deploy.sh` opens no tunnel of its own and needs no bastion.
Through a tunnel you run yourself, that origin is the app's, `$ORIGIN` (README.md, "Before any
note: the operator session").

Without `--url` it needs the bastion, and it never turns the bastion on or off: when the outputs
document names no bastion it refuses before anything is built, exit 2, with a line naming
`bastion = true` and `--url`. Should the apply leave no bastion all the same, because
`terraform.tfvars` set it false after the document was last written, the wait fails, exit 1, with
the same line, before it opens a tunnel.

Exit 0 means the new gateway runs steady and healthy. Exit 1 names the step that failed; exit 2
means it did not start (a usage error, an entry value unset, a missing tool, no Region, no
credentials, no outputs document, or one that names no bastion without `--url`).

Then check it yourself from the workstation, through a tunnel of your own: the one `deploy.sh`
opened is closed by the time it exits. The tunnel's hop is the bastion's task, which the deployment
does not replace: it replaces the gateway's task definition only. If a tunnel you started before
the deployment is still up, use it; if it has ended, start `tools/scripts/tunnel.sh` again. Then

```bash
tools/scripts/verify-tunnel.sh /healthz
```

On one task with sessions in memory, every user signs in again on their next request. On two tasks
with the durable session store, every user stays signed in.

## If it fails

- **Before the apply** (steps 1 and 2): nothing the cell runs changed (step 2 may have pushed an
  image to ECR, under a tag of its own, which nothing uses). Fix the cause and run it again.
  A step-1 refusal means the registry is empty: publish an app with `spactl`, its release and then
  its entry, with the platform configuration (`$CONFIG`) the README builds, `RELEASE` set to the
  release's name and `RELEASE_DIRECTORY` to the directory of its built files, and run it again:

  ```bash
  spactl release publish "$APP" "$RELEASE_DIRECTORY" "$RELEASE" --outputs "$SPA_OUTPUTS_FILE"
  spactl app add "$APP_DEFINITION" --release "$RELEASE" --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
  ```

- **The apply or after it** (steps 3 to 5): the deployment circuit breaker rolls a deployment whose
  tasks fail their health checks back to the previous task definition on its own. If the service is
  steady but on the wrong gateway, or you want the previous one back, use [rollback.md](rollback.md).
