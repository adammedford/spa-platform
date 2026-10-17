# 01: One local check for the pre-push hook and the overnight run

**Owner:** integrator
**Blocked by:** None (can start immediately).
**Status:** ready-for-agent

**What to build:** The checks a branch must pass on this machine live in one script, and everything
that checks a branch runs that script as written: the pre-push hook, a person before a push, and
every lane of the overnight run.

Today there are two copies. The pre-push hook holds A-05, the steering check, the roadmap check,
the duplicated-artifact check, the build, and Terraform's format and validate. The overnight
workflow holds its own build-and-test command as a string inside a JavaScript template literal,
pasted into every lane's prompt. On the 2026-10-17 run that string wrote `\;`, which a template
literal renders as a bare `;`, and zsh refused to parse the whole command. Every lane had to repair
it before it could run; 15 agents hit the error first and spent about 85 tool calls on it. The
one-character fix went into the workflow the same morning; this ticket removes the second copy.

- The hook's checks move into the script unchanged, and the hook calls it: same checks, same skips
  (no build when no .NET source changed, no Terraform when nothing under `infra/` did), same
  messages.
- A full mode adds what CI runs and the hook leaves to CI: the .NET test suite, the smoke test on
  a port the caller names, and `terraform test` for every root and module that has tests. It ends
  on one line saying pass or fail and exits non-zero on failure, so a log's last line is the
  verdict.
- The script carries a bash shebang and runs under the bash 3.2 macOS ships, so it behaves the same
  whatever shell calls it.
- CI fails when a tracked shell script does not parse, so a broken script fails at its first push
  rather than inside a lane.
- The overnight workflow's build-and-test step runs the script instead of the pasted command. That
  workflow is a local file, ignored by git, so the session that lands this ticket makes the edit in
  Adam's checkout.

Not called a gate: GLOSSARY.md reserves Gate for G1a to G5. Overnight ticket 69 is building a test
harness for the repository's shell scripts; this script's cases belong there once it lands.

- [ ] The hook's output is unchanged for a docs-only push, a .NET change and a change under `infra/`.
- [ ] The full mode ends green on `main`, called from a zsh login shell and from bash 3.2.
- [ ] A deliberately broken copy of a tracked shell script fails CI's parse step.
- [ ] The overnight workflow's build-and-test step calls the script, and the next run's lane logs
      show it ran unedited.
