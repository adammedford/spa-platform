# S3 layout

- **Freezes at:** G1a
- **Consumed by:** workstreams 1 and 2
- **Source:** `docs/plan/02-contracts.md` → S3 layout; ADR-0009

One private bucket per cell holds the registry and every release.

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

- **A release prefix is written once.** `spactl` sends `If-None-Match: *` on every object, in
  single-part uploads. The bucket policy requires that header under `apps/` and denies deletes there
  to everyone but the teardown role. Without the delete rule, a delete followed by a write would
  replace a release. The bucket enforces this, not gateway code; P-04 checks it.
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
