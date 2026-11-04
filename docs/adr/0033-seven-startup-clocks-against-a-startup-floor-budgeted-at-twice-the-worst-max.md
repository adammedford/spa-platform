# ADR-0033: Time a gateway cold start by seven startup clocks against a startup floor, and budget each clock at 2 × the worst max of its basis

- **Status:** Accepted, with the deployed column empty. Three routes change it later:
  1. #178's deployed values arrive as a dated status-line amendment, as ADR-0005 and ADR-0017 were
     amended. They reach this ADR through a follow-up integrator ticket blocked by #177 and #178
     (#286 Q147).
  2. A new local basis (new runs on a stated machine, the same rule applied) is also a status-line
     amendment.
  3. Changing the 2× multiplier is a rule change, so it takes a new ADR that supersedes this one's
     rule section.
- **Date:** 2026-10-29
- **Deciders:** integrator, on Adam's answers to #175, #176, #246 and #177 (wayfinder map #169)
- **Plan ref:** No decision row. Map #169 and its tickets #170 to #178; `docs/plan/04-tests.md`
  gains `B-` and tier L at the next re-export. #177's grill, confirmed as Q111 to Q127 on #286, with
  Q112 and Q115 as overridden there; #252's grill, confirmed as Q128 to Q131 on #286; #178's grill,
  confirmed on #286, where Q134, Q137, Q139, Q146 and Q147 bear on this record.

## Context

The wayfinder map #169 asked how a gateway cold start is timed, what the least a start could cost
is, and how much a start may take before the gateway is tuned. Its tickets answered it in turn:
#170 chose the local stand-ins, #171 read what ECS and the ALB expose, #173 built the startup
benchmark under `tools/bench/` and the floor app beside it, #174 ran both and recorded the first
numbers, #175 set the budget per clock, #176 named the test id and its tier, and #246 decided that
workstream 6 owns `tools/bench/`. #177 asked for one ADR recording all of it, and #178, the Fargate
run, fills the deployed column afterwards.

Two corrections to the ticket's words are made here. #177 said "the five startup clocks": the
answers name seven, because listening is reported only, the Fargate 302 is a clock of its own, and
two Fargate clocks start from a zero of their own. #177 said "#178 may revise the budget": #178
fills the deployed column by the same rule and leaves the local budgets alone.

The thing under `tools/bench/` is **the startup benchmark**, never "harness" (#252, confirmed as
#286 Q111 and Q128): `GLOSSARY.md` → "Harness" is a closed list of two senses, and workstream 6
owns both the gateway test harness and `tools/bench/`, so a bare "the harness" in its texts would
name either. `tools/bench/startup.py` and `startup_tests.py` took the name under #306 (`29621ed`).

## Decision

**A startup clock** is one named interval of a gateway cold start, from a stated zero to the first
observation of a stated event (#286 Q112). There are seven. Each row says what is measured
deployed.

| Clock | Zero | Stops at (local) | Deployed | Budget |
| --- | --- | --- | --- | --- |
| listening | container `StartedAt` | first TCP connect to :8080; Kestrel's "Now listening on" as cross-check | Kestrel's "Now listening on" log line | reported only |
| healthy | container `StartedAt` | first `/healthz` 200, new connection per attempt | not measured deployed (nothing on the cell calls `/healthz`; #286 Q137) | 4 s local |
| ready | container `StartedAt` | first `/readyz` 200 | the gateway's "Registry revision … is live" log line; the first passing `/readyz` check is reported beside it, quantized to the 30 s check | 6 s local; deployed set by #178 |
| first app-host 401 | container `StartedAt` | first 401 to a cookieless `GET /` with `Sec-Fetch-Mode: cors` on a registered host | not measured deployed (#286 Q134) | 7 s local |
| first app-host 302 | that request's arrival | — | the new task's first navigation's logged elapsed (#286 Q139); its mark from `StartedAt` is printed and labelled as bounded by target healthy | deployed set by #178 |
| pull-and-place | task `createdAt` | — | task `startedAt` (`describe-tasks`) | deployed set by #178 |
| target healthy | task `startedAt` | — | first poll that shows the target healthy | bounded by the cell's third check (T0 + 60 s) until #178 |

**The startup floor** is the time the floor app (`tools/bench/floor/`) takes to reach the listening
and healthy clocks, measured in the same run as the gateway. It is a reference and never a budget,
and it exists only for those two clocks: the floor app has no `/readyz` and no app host (#286
Q113). "Startup floor" is the measurement; "floor app" is the artifact, a term of this ADR and not
of the glossary, because it names a path.

**The method** (#286 Q119): one cold start is a fresh container from an image already pulled, under
`--cpus 0.25 --memory 1g`; N=10, floor and gateway alternating; clock zero is `StartedAt`, and marks
come from a probe on the same kernel clock, polling every 10 ms; moto 5.2.4 stands in for S3 and
Parameter Store; a static discovery document on `127.0.0.1:9000` in a shared namespace stands in
for the provider. The local 401 needs that stand-in because the OpenID Connect handler reads
discovery before `OnRedirectToIdentityProvider` writes the 401; without it the answer is a 500
(#173). Its reversal trigger is change request mr-8 refusing a non-https authority. The median is
the headline and the max is the check.

**The budgets** (#175): an absolute budget in seconds per clock, set at 2 × the worst max over the
basis and rounded up to the whole second. The basis is both runs of #174, 20 starts, no warm-up
discard, marks as reported, on an ARM64 workstation under `--cpus 0.25 --memory 1g`. The local
budgets are 4 / 6 / 7 s for healthy, ready and the first app-host 401. The deployed column is
empty: #178 sets it by the same rule with the exceptions its answer names (#286 Q146: 2 × worst max
over the ten, rounded up to the second, for pull-and-place, ready and the 302's logged elapsed;
listening stays reported only; the first passing check and target healthy are stated as check
counts after T0, with the third check as the hard bound). A local budget never stands in for a
deployed one, and the three container clocks are not held to the local budgets on Fargate (#175
Q4). `deploy.sh` and `spactl wait` sit inside the budget and are not driven by it (#175 Q6).

**A startup budget** is therefore the most a gateway cold start may take to reach one startup
clock, checked against the slowest start in a run, and set per environment (#286 Q114).

**The grace relation** (#286 Q122) is written over the deployed ready budget: that budget plus
max(0, S − T0) must fit inside T0 + 60 s, where T0 is the first health check and S the task's
`startedAt` (#171, #175 Q5). The local 6 s is shown only as an illustration (a margin of about
54 s, less S − T0). The relation is marked unchecked until #178's amendment, which either confirms
the 60 s grace or raises an infra change request. The breaker relation (#175 Q5): the circuit breaker counts failed
tasks, not seconds, so no budget reaches it. No setting changes under this ADR.

**The startup benchmark** lives at `tools/bench/` and is owned by workstream 6, the floor project
`tools/bench/floor/Bench.Floor.csproj` with it; `SpaPlatform.slnx`, which lists that project, stays
the integrator's (#246). Its tests, `tools/bench/startup_tests.py`, are its test companion.

**The test id** (#176): a new class `B-` (startup benchmark), not `P-`, because startup time is
something gateway code can break. `B-01` covers the container clocks: healthy, ready and the first
app-host response, the 401 locally and the 302 added when deployed. `B-02` covers the two Fargate
clocks, pull-and-place and target healthy, deployed only. The floor is reported beside `B-01`, never
asserted. A third tier, **Local (L)**, runs a fresh container of the built image under the task's
limits, against the stand-ins, on a workstation or a CI runner; hermetic keeps its in-process
meaning. `B-01` runs in L and D, `B-02` in D. The assertion: the max of N=10 per budgeted gateway
clock is at or under its budget; the median is the headline only; an unreached clock fails the run;
marks are asserted raw with the precision window printed; N is fixed at 10 for a verdict. A breach
is a run whose max is over a clock's budget, or any start that never reached the clock, and a pass
counts regardless of load; when a run's own floor control shows a loaded machine (the floor's
`/healthz` max above about 1.2 s) the run gives no verdict, exactly one re-run gives it, and both
runs' files are kept (#175 Q7). Cadence: L runs nightly on `main` and on demand, in a CI job of its
own on `ubuntu-24.04-arm`, informational until ten calibration runs fit the budget with its
headroom, and never in the x64 build job and never in `tools/check.sh`; D runs `B-01` and `B-02` at
G3, at every later gate, and after any change to the task's size or image chain, attached as
evidence, joining a gate's exit test from the first gate after the Fargate budgets exist, G4 at the
earliest. Reporting: a `bench: pass:` or `bench: fail:` last line and a non-zero exit on fail once
the job is promoted; the `.txt` and `.json` kept as a CI artifact or committed under
`tools/bench/results/` as gate evidence; CI files no issue; no retry-until-green.

**The proof exception** (#286 Q123): a `B-` id is proved by the startup benchmark's verdict on its
tier, never by a test project, and the benchmark's logic by its test companion. This is an
exception to `GLOSSARY.md` → "Test ids", which gains the clause, and not to `AGENTS.md`, whose
proof rule applies to ids under a task's "Done when", which no `B-` id is.

**The tuning trigger** (#286 Q120, Q121): any valid breach of a budgeted clock opens the tuning
effort, as change requests to integrator paths. A breach counts only on an asserting run: a
workstation L run, a CI L run after promotion, or a D run once #178 has filled the column. An
informational CI breach prompts a workstation run and is not itself the trigger. Diagnosis is per
clock: for healthy, the gap over the startup floor against the basis's gap, where a grown gap means
a gateway regression and a grown floor means the image chain or the machine; for ready and
app-host, the gateway's own split from healthy to that clock against the same split in the basis.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Five clocks, as the ticket said | #175 Q1 already names more: listening reported only, the Fargate 302, and two Fargate clocks with their own zeros (#286 Q112). |
| A budget relative to the floor (floor plus an allowance) | The floor is a reference only, and its gap is the diagnosis of a breach, not a second budget (#175 Q2); it also exists only for listening and healthy (#286 Q113). |
| A tighter multiplier, such as 1.5× | 2× is loose (a gateway that doubled its start would pass), accepted while CI noise is unmeasured; the multiplier is looked at again when #176's calibration runs exist, by a new ADR (#175 Q2; #286 Q117). |
| Two ADRs, measurement and test vocabulary apart | #176's assertion reads #175's budget and its G4 timing depends on #175 Q4; one ADR keeps them together, and value changes already go by amendment (#286 Q116). |
| #178 revising the budget in place | Values arrive as status-line amendments; only a rule change takes a new ADR (#286 Q117). |
| No discovery stand-in for the local 401 | The handler reads discovery first, so the 401 becomes a 500 (#286 Q119). |
| A floor gap as the diagnosis for every clock | No floor exists for ready or app-host; their diagnosis is the gateway's post-healthy split (#286 Q120). |
| Any CI breach as the trigger | The CI ARM64 run is informational until promoted, and the budget was set on a workstation (#286 Q121). |
| Checking the grace relation against the local 6 s | A local budget never stands in for a deployed one (#286 Q122). |
| A `P-` id, or a third sense of "harness" | A new `B-` class and tier L, and "the startup benchmark" as the name (#176; #286 Q111). |
| Holding Fargate to the local numbers until #178 | Nothing measured them there; the deployed column stays empty, and the round's 60 s placeholder for pull-and-place was dropped (#175 Q4). |

## Consequences

- Two follow-ups, named by title and filed by the session that lands the ADR (#286 Q125): the
  verdict mode in `startup.py` (workstream 6), and the ARM64 nightly job (integrator), which the
  verdict mode blocks. Until the verdict mode lands, `startup.py` exits 0 on every path and no `B-`
  id can be asserted.
- Open, not decided, with the map (#169) as their home (#286 Q126): the consumers' settings once the
  Fargate numbers exist, and the discovery-down fault run (MR-5).
- `GLOSSARY.md` gains **Startup clock**, **Startup floor**, **Startup budget**, **Startup
  benchmark** and **Local (L)**, `B-` in **Test ids** with the proof exception, and the **Harness**
  _Avoid_ clause, in one commit beside this ADR (#286 Q115 as overridden).
- `docs/plan/04-tests.md` gains `B-` and tier L at the next re-export; the plan is not edited.
- #252 closes when the workstream 6 rename commit (`29621ed`, under #306) and this ADR's commit are
  both on main; the issue-172 change request is not edited (#286 Q127, Q131).
- The map's "Not yet specified" list shrinks to the two open items above.

## Against existing ADRs

`docs/adr/` on origin/main, 0001 to 0032, was searched for "harness", "tier", "hermetic", "test
id", "grace", "startup", "cold start", "budget" and "amend". The draft confirmed on #286 had been
checked against 0001 to 0028; 0029 to 0032 landed after it and were read for this commit.

- ADR-0002's "at a cold start there is no snapshot to keep" uses "cold start" in the same sense.
- ADR-0005, ADR-0017 and ADR-0024 speak of Cognito's refresh-token grace window, a different grace
  from the ECS health-check grace period here.
- ADR-0027 says "the glossary reserves the word" harness, which this ADR keeps by naming the startup
  benchmark.
- ADR-0029, ADR-0030 and ADR-0031 name none of these terms.
- ADR-0032 decision 7, accepted 2026-10-28, says that from then on only a new ADR amends an ADR,
  recorded by a Status-line note on the older one. Routes 1 and 2 of this ADR's Status were
  confirmed on 2026-10-22 (#286 Q117) and are recorded as confirmed: a value change is a dated
  Status-line note on this ADR, and a rule change is a new ADR. Whether the note that carries
  #178's values is itself backed by a new ADR is settled by ADR-0032 as it stands when the
  follow-up ticket of #286 Q147 is worked; this ADR does not amend ADR-0032.

Two non-ADR texts it does contradict, and says so: the glossary's **Test ids** ("the owning
workstream proves it in its own project") gains the `B-` exception in the same landing; and
`docs/plan/04-tests.md`'s two tiers, which wait for the next re-export. `AGENTS.md` is not edited
(#286 Q123).

## Evidence

- #175's Answer, the budget per clock:
  https://github.com/adammedford/spa-platform/issues/175#issuecomment-6329159624
- #176's Answer, the test id, tier, assertion and cadence:
  https://github.com/adammedford/spa-platform/issues/176#issuecomment-6329159840
- #246's Answer, `tools/bench/` to workstream 6:
  https://github.com/adammedford/spa-platform/issues/246#issuecomment-6331085848
- #177's grill and this record as confirmed, Q111 to Q127 on #286:
  https://github.com/adammedford/spa-platform/issues/286#issuecomment-6344559207
- The confirmed draft this file is written from, "ADR candidate, as confirmed" on #177:
  https://github.com/adammedford/spa-platform/issues/177#issuecomment-6345156548
- #174's runs, the basis: `tools/bench/results/`.
