# 02: The contracts index agrees with each contract's own freeze header

**Source:** `.scratch/overnight/issues/66-cross-reference-checks-and-land-body.md`
**Owner:** integrator

**What to build:** A freeze gate claimed in `contracts/README.md`'s index and denied by the
contract it points at fails before it is pushed. Today the index carries a "Freezes at" cell per
row and each contract carries its own freeze header, and nothing compares them: the Pipeline row
once landed asserting a freeze at a gate its target never froze at, contradicting
`contracts/pipeline.md` two directories away. After this ticket the disagreement is a failure that
names both sides, so an author can see which of the index and the header is wrong without opening
either.

The rule lands in `.github/steering.py`. It is already wired into the pre-push hook's one-second
tier and into CI, its docstring already carries the rule this is an instance of -- "No dangling
pointer" -- and its machinery already resolves paths against the tree. A cell that disagrees with
the file it indexes is the same defect as a pointer that resolves to nothing: a claim about the
repository the repository does not support.

A cell is allowed to say more than the header. `G1a; stage order changed at G1b` and `G1a, except
the oauth section at G1b` are both legitimate, so the comparison is on the leading gate token. A
cell naming a different gate than its target, or naming a second gate as the freeze rather than as
a change, is the failure.

**Blocked by:** 01 (the test companion). Shares a file with 03 and 04, so they land one at a time,
but neither gates this.

**Status:** ready-for-agent

- [ ] The check parses the index table in `contracts/README.md`, and fails when a row's leading
      gate token differs from its target's own freeze header.
- [ ] The failure message names the row, the cell, the target file and the header, so neither side
      has to be opened to see the disagreement.
- [ ] The two header shapes in the tree are both read: the markdown contracts' `**Freezes at:**`
      line, and the `Freezes at <gate>.` sentence inside a JSON schema's `description`.
- [ ] A relational cell that names no gate -- `With the registry document`, `With the interface
      each one stands in for` -- is not a failure.
- [ ] A row whose target is source code rather than a contract file, and so carries no header, is
      not a failure.
- [ ] A red case per shape above, built from a row shape actually in the tree, and a green case on
      the current index, in `.github/steering_tests.py`.
- [ ] `steering.py`'s module docstring and the CI step's name both describe the wider charter; the
      current name describes only the two steering files. Later rules keep both accurate.
- [ ] The check still reports that it did not run when `python3` is absent, matching how the
      existing hook checks degrade.
