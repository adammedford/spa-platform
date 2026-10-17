# 07: `tools/land.sh` accepts a body that is not a regular file

**Owner:** integrator
**Blocked by:** None (can start immediately).
**Status:** ready-for-agent

**What to build:** A merge body produced on the fly can be handed to `tools/land.sh` the way `gh`
itself would take it. Ticket 05 validates `--body-file` with `[ -f ]`, so a body passed by process
substitution (`--body-file <(...)`, which arrives as `/dev/fd/N`) or through a named pipe is
refused, though `gh pr merge --body-file` reads both.

`-` must stay refused: the script reads standard input for its "no subject given" confirmation,
so a body on standard input would race it. That narrowing is deliberate; keep it, and say why
beside the check.

Note the path is made absolute before the script changes directory (`2c9962f`). A `/dev/fd/N`
path is already absolute, but the descriptor is the caller's, so confirm it is still open when
`gh` reads it.

- [ ] A readable FIFO and a `/dev/fd/N` path are accepted; a directory, a missing path and an
      unreadable file are still refused before the merge.
- [ ] `--body-file -` is refused with a message saying why.
- [ ] Verified under the bash 3.2 macOS ships, by running the script's own parse loop.
