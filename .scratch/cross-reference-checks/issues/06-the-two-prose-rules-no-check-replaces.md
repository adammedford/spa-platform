# 06: The two prose rules no check replaces

**Source:** `.scratch/overnight/issues/66-cross-reference-checks-and-land-body.md`
**Owner:** integrator

**What to build:** The reviewer agent enforces the two judgement calls that cost this session six
review rounds, so the next contract sentence of the same shape is caught by reading rather than by
four rounds of fixing the fix.

One false sentence in the contracts -- that every rejection reaches `security.rejections`, when the
method allowlist's 405 does not -- survived in nine places across four files, and every round found
a new defect in the fix rather than in the original claim. Two of those defects are not decidable
from the tree, so no check substitutes for them:

- **Scoping.** A sentence stating a universal names its exceptions by the *writer*, never by an
  identifier: an identifier may have several writers and only some be excepted. The qualifier that
  cost four rounds was scoped to a rule id that two stages write when only one was excepted.
- **Search discipline.** Grep the noun a claim cannot paraphrase away, then verify that the claim's
  scope is the thing that actually has the property. Greping the sentence finds one phrasing;
  greping the noun found all nine sites and ended the cycle.

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] `CODING_STANDARDS.md` carries the scoping rule, in the voice of the rules already there, with
      the reason an identifier is the wrong scope.
- [ ] `docs/agents/review.md` carries the search discipline as a step a reviewer performs, not as
      advice.
- [ ] Both state why they are prose and not a check: they are judgement calls about cross-file
      consistency, which is what `CODING_STANDARDS.md` is for, and the three rules in tickets 02 to
      04 are the part that was decidable from the tree.
- [ ] No check is added here, and neither addition restates one.
