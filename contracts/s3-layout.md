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
empty for the default, and set to the same value when the cell module overrides the name. An
override changes only what comes before the -<account id>-<region>-an suffix: the bucket is always
created in the account regional namespace, and the cell module's plan fails on any other name.

```text
registry/registry.json                                              one object, every version kept
apps/{appId}/releases/{release}/index.html                          required, written last
apps/{appId}/releases/{release}/assets/**                           immutable, cached for a year
apps/{appId}/releases/{release}/**                                  other files, revalidated on every use
apps/{appId}/claims/assets/**                                       one claim per shell asset key, never served
apps/{appId}/fragments/{fragmentId}/releases/{release}/{entry}      required, written last
apps/{appId}/fragments/{fragmentId}/releases/{release}/assets/**    immutable, cached for a year
apps/{appId}/fragments/{fragmentId}/releases/{release}/**           other files, revalidated on every use
apps/{appId}/fragments/{fragmentId}/claims/assets/**                one claim per fragment asset key, never served
```

`{appId}`, `{fragmentId}` and `{release}` are the registry entry's values, and `{entry}` is its
fragment's `entry`, `entry.js` when the entry names none; all four are constrained by
`registry.schema.json`: the key is built from registry values and a validated request path, never
from an unchecked path segment. A fragment's request path maps to that fragment's live release, not
the shell's: `/fragments/{fragmentId}/{rest}` maps to
`apps/{appId}/fragments/{fragmentId}/releases/{release}/{rest}`, with `{release}` the fragment's own,
under the same cache rules by path as a shell's, applied to `{rest}`, and with no `index.html`
fallback. No request path maps to a claim, nothing under `/fragments/` maps into the shell's
release, and `/fragments/{fragmentId}/api/**` maps to no key at all: it is the fragment's APIs
(`contracts/upstream-contract.md`), so `spactl fragment publish` refuses a release with a top-level
`api/`.

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
- **Activation is a registry change.** Setting `release` on an entry, or on one of its
  `fragments[]`, makes that release live within one poll interval, for the requests that follow; a
  fragment's activation leaves its shell's release alone. Rolling back is the same change in
  reverse.
- **An `assets/` URL names the same bytes in its namespace, across every release.** A namespace is
  a shell's, `apps/{appId}/`, or one of its fragments', `apps/{appId}/fragments/{fragmentId}/`. The
  URL carries no release and is cached for a year as `immutable`, so a later release that put other
  bytes behind it would leave a browser running a cached chunk that is wrong. `spactl` holds the rule
  with a claim per `assets/` key: before it writes `releases/{release}/assets/{path}` it creates
  `claims/assets/{path}` in the same namespace with `If-None-Match: *`, its body a JSON object of
  `sha256`, the object's SHA-256 as 64 lowercase hexadecimal digits, and `release`, the release that
  made the claim. Of two publishers racing for one key, exactly one creates the claim. A claim that
  already exists with the same digest lets the publish go on, which is how a stopped publish
  resumes; one with another digest refuses the release, naming the release that holds the claim,
  and no object is written under that key. A claim that cannot be read back after its create was
  refused is answered "run the command again". A failed publish leaves its claims, so its URLs stay
  bound to its bytes for good: that is the rule working, not a leak. The bucket policy's
  `If-None-Match` and delete rules under `apps/` make each claim write-once, as they make each key
  under a release. Claims are never served. The rule holds for `release publish` as for
  `fragment publish`, with no backfill: a key published before claims existed, and any object
  written to the bucket other than by `spactl`, has no claim and is outside the rule.
- **A fragment release is complete when its entry file exists.** `spactl fragment publish` requires
  the file its `--entry` names (`entry.js` by default) at the release root and writes it after every
  other key of the release, so a prefix that holds its entry file holds the rest. Every registry
  write checks every shell and every fragment of the document being written: each shell's
  `index.html`, and each fragment's `apps/{appId}/fragments/{fragmentId}/releases/{release}/{entry}`
  with `entry` from that document; a write naming a release without its file is refused. Completion
  holds only when the release was published with the `--entry` the definition names; a mismatch is a
  residual, not a check.
- **Content type comes from the file extension.** The gateway ignores the type stored in S3 and
  sends `nosniff`. An unknown extension is served as `application/octet-stream`.
- **Cache policy follows the path.** `index.html`, a fragment's entry file and non-asset files are
  `no-cache` with an ETag. Files under a release's `assets/`, a shell's or a fragment's, get one year
  and `immutable`, which the same-bytes rule above makes safe. Every response is `private`, because
  every asset needs a session (ADR-0008).
- **The gateway cannot write.** The task role reads this cell's bucket prefixes; it has no write to
  the registry (P-03). `spactl` writes, with the credentials of whoever runs it.

## Who reads and writes what

| Key prefix | Gateway | `spactl` | Terraform |
| --- | --- | --- | --- |
| `registry/registry.json` | conditional GET | GET, and PUT conditional on the live version | creates the bucket, writes nothing |
| `apps/{appId}/releases/{release}/**` | GET | PUT with `If-None-Match: *`; at every registry write, a read of `index.html` | nothing |
| `apps/{appId}/fragments/{fragmentId}/releases/{release}/**` | GET | PUT with `If-None-Match: *`, the entry file last; at every registry write, a check that the entry file exists | nothing |
| `apps/{appId}[/fragments/{fragmentId}]/claims/assets/**` | nothing: no request path maps to it | PUT with `If-None-Match: *` before the asset it claims; GET when that PUT finds a claim | nothing |

`spactl registry rollback` reads S3's version history, so the registry object's versioning is part
of this contract and not an operational detail.
