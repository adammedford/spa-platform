# 02: The local check runs Terraform at CI's floor version

**Owner:** integrator
**Blocked by:** 01 (the local check script).
**Status:** moved to #69

**What to build:** A Terraform change checked on this machine is checked by the same Terraform as
CI. CI pins 1.11.0, the repository's chosen floor, and every root requires at least it. S3 native
state locking first shipped in [Terraform 1.10.0](https://github.com/hashicorp/terraform/blob/v1.10.0/CHANGELOG.md);
1.11.0 remains this repository's minimum. This Mac runs 1.16.5, so the local check validates and tests with a newer
release than CI does, and anything 1.11 refuses that a later release accepts would pass here and
fail in CI.

On the 2026-10-17 run all six infra agents, the implementers and reviewers of tasks 1.1, 1.2 and
1.5, downloaded 1.11.0 into a scratch directory and ran their Terraform tests on both versions, so
that a result here would hold in CI. That took 36 tool calls, repeated lane by lane.

- One file names the floor version. CI's Terraform job and the local check both read it, so
  raising the floor is a one-line change.
- `tools/preflight.sh` reports whether the floor version is installed, and how to install it if not.
- With the floor version installed, the local check formats, validates and tests every root and
  module with it. Without it, the check says which version it used instead, so a green result
  never hides the substitution.

Git is out of scope: CI takes whatever git its runner image ships (2.55 on 2026-10-17, against
Apple's 2.54 here), so there is no pinned version to match. That gap is why overnight ticket 71's
last box closes in CI rather than here.

- [ ] CI's Terraform job reads its version from the file, and its runs are otherwise unchanged.
- [ ] Preflight names the floor version and says whether this machine has it.
- [ ] With the floor installed, the local check's Terraform output names 1.11.0.
- [ ] Without it, the local check's output names the version it used.
- [ ] No lane in the next overnight run downloads a Terraform binary.

## Comments

- 2026-10-17: moved to #69 under ADR-0020; this file is the archive copy.
