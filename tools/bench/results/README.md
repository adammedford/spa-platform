# Startup benchmark results

Each file here is one run of `tools/bench/startup.py` as it was printed (`.txt`) and its `--json`
output (`.json`), with every cold start's marks and the facts: commit, image ids, Docker, host.

| Run | Ticket | Commit | Conditions |
| --- | --- | --- | --- |
| `2026-10-21-run-1` | #174 | ffbee6750fc9 | Apple M4 Pro, Docker Desktop 4.96.0 (engine 29.9.1), `--cpus 0.25 --memory 1g`, N=10. Shared machine: other overnight lanes were building, load average 7 to 8. The gateway clocks climb from run 4 to run 10. |
| `2026-10-21-run-2` | #174 | ffbee6750fc9 | The same images (`--no-build`), about two minutes later (121 s from run 1's last container start to run 2's first). The load average went from 2.3 at the start to 10 at the end. No climb across the runs. This is the table #174's Answer reports. |

Invocation, from the worktree root:

```bash
PATH=$HOME/.docker/bin:$PATH python3 tools/bench/startup.py --json <file>             # run 1
PATH=$HOME/.docker/bin:$PATH python3 tools/bench/startup.py --no-build --json <file>  # run 2
```

Neither run was on a quiet machine. Treat them as first rough numbers, not as a budget.

The /healthz precision line is the first request's own round trip, not a stalled earlier attempt:
in all 40 cold starts /healthz returned 200 on the first request sent after the listening mark
(`attempts: 1` in the `.json`). That request took 121 to 306 ms on the floor and 384 to 701 ms on
the gateway, so the floor's /healthz mark can sit up to about 0.3 s after the event and the
gateway's up to about 0.7 s.
