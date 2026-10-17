# 08: A relational freeze cell is compared too

**Owner:** integrator
**Blocked by:** None (can start immediately).
**Status:** ready-for-agent

**What to build:** Every contract the index lists has its own freeze header held against something.
Ticket 02 passes a cell that names no gate, such as the App definition row's "With the registry
document", so `app-definition.schema.json`'s own header is never compared with anything. If that
header and the registry row ever disagree, nothing fails.

Resolve a relational cell to the row it names, and compare the target's header against that row's
gate. A relational cell naming no row that exists is itself a failure.

- [ ] The App definition row is compared through the Registry document row, and the success
      line's count of compared headers rises to include it.
- [ ] A red case where the relational target's header disagrees with the row it defers to, and one
      where the cell names a row that does not exist, in `.github/steering_tests.py`.
- [ ] The Test doubles row ("With the interface each one stands in for") names no single row; decide
      whether it stays exempt, and say so in the rule's comment.
