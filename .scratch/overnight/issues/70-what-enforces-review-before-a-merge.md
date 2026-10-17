# 70: What enforces review before a merge

**Task:** none (raised by overnight ticket 66, "Out of Scope")
**Owner:** integrator
**Blocked by:** None
**Status:** ready-for-human

## Problem

`docs/agents/landing-work.md` requires a pull request to be reviewed by a session other than the
one that wrote it, and says the integrator's own work "merges on CI plus the human. That is the
weakest link here, and it is known." Nothing enforces either. In the session ticket 66 records, two
pull requests merged without a completed review and one was tagged while its review was still
running, which is how a false guarantee came to be frozen in `contracts-v1`.

## The decision

The options differ in kind, which is why this is a decision rather than a task:

- **A check in `tools/land.sh`** — refuse to merge unless the pull request carries an approving
  review or a review-evidence line. Cheap, and holds only for merges through the script.
- **A branch protection rule** requiring an approving review — holds for every merge, but on a
  single-account repository the author cannot approve their own pull request, so it needs a
  second account or a bypass, and a bypass is the weak link again.
- **Accept it as the documented weak link** — and say in `landing-work.md` what a tag must wait for,
  since the tag mid-review was the costliest instance.

## Done when

- [ ] One option is chosen and recorded as an ADR under `docs/adr/`.
- [ ] `landing-work.md` states the rule, including whether a gate tag may be cut while a review is
      open.
