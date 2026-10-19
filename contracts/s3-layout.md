# S3 layout

- **Freezes at:** G1a
- **Consumed by:** workstreams 1 and 2
- **Source:** `docs/plan/02-contracts.md` → S3 layout; ADR-0009

One private bucket per cell holds the registry and every release. Its default name is
`spa-platform-<cell>-<account id>-<region>-an`, in S3's account regional namespace, so no other
account can create it between a teardown and the next deployment (change request 1.5a). The cell
module can override it, so every consumer but one **reads** the platform root's `bucket` output and
never derives the name: `spactl`, the scripts and the task environment alike. The one that derives
it is `infra/bootstrap`, for the teardown role's and the CI plan role's grants, because it applies
before the bucket exists. Its `platform_bucket_name` variable keeps it in step with the cell module:
empty for the default, and set to the same value when the cell module overrides the name.

```text
registry/registry.json                         one object, every version kept
apps/{appId}/releases/{release}/index.html     required
apps/{appId}/releases/{release}/assets/**      immutable, cached for a year
apps/{appId}/releases/{release}/**             other files, revalidated on every use
```

`{appId}` and `{release}` are the registry entry's values, and both are constrained by
`registry.schema.json`: the key is built from registry values and a validated request path, never
from an unchecked path segment.

## Rules

`infra/bootstrap` creates the teardown role ADR-0014 specifies (task 1.5). Nothing here attests to
an applied stack: P-04 checks the policy against the applied bucket at G3.

- **Each key under a release is written once.** A release can gain a key, but outside teardown it
  never changes or loses one: the bucket enforces write-once per key, not per prefix, and `spactl`'s
  resumable publish adds keys to an existing prefix. `spactl` sends `If-None-Match: *` on every
  object, in single-part uploads. The bucket policy requires that header under `apps/` and denies
  deletes there to everyone but the teardown role. Without the delete rule, a delete followed by a
  write would replace a release. The bucket enforces this, not gateway code; P-04 checks it.
- **The teardown role is `spa-platform-<cell>-teardown`**, created by `infra/bootstrap` and so
  outliving what it tears down (ADR-0014). Its **name** is derived from the cell, never configured;
  its ARN comes from AWS, because the platform root looks the role up rather than assembling the
  string. The value the policy names is the **role** ARN, never the
  `arn:aws:sts::<account>:assumed-role/<role>/<session>` form that `aws sts get-caller-identity`
  prints: `aws:PrincipalArn` carries the role ARN for a role session, so the session form would
  match nothing and the deny would apply to teardown as well. The role may delete under `apps/` and
  holds **no write permission at all**, which is what stops it replacing a release: a conditional
  write succeeds when the current version is a delete marker, so the `If-None-Match` rule alone would
  not stop a principal that could both delete and write. P-04 runs as its own principal and not as
  this role. A trusted human who can delete with this role and then write with another principal
  can republish; this exception is break-glass access, not tamper-proof retention.
- **Activation is a registry change.** Setting `release` on an entry makes it live within one poll
  interval. Rolling back is the same change in reverse.
- **Content type comes from the file extension.** The gateway ignores the type stored in S3 and
  sends `nosniff`. An unknown extension is served as `application/octet-stream`.
- **Cache policy follows the path.** `index.html` and non-asset files are `no-cache` with an ETag.
  Files under `assets/` get one year and `immutable`. Every response is `private`, because every
  asset needs a session (ADR-0008).
- **The gateway cannot write.** The task role reads this cell's bucket prefixes; it has no write to
  the registry (P-03). `spactl` writes, with the credentials of whoever runs it.

## Who reads and writes what

| Key prefix | Gateway | `spactl` | Terraform |
| --- | --- | --- | --- |
| `registry/registry.json` | conditional GET | GET, and PUT conditional on the live version | creates the bucket, writes nothing |
| `apps/{appId}/releases/{release}/**` | GET | PUT with `If-None-Match: *` | nothing |

`spactl registry rollback` reads S3's version history, so the registry object's versioning is part
of this contract and not an operational detail.
