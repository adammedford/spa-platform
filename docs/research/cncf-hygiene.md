# CNCF Sandbox, OpenSSF Best Practices at passing and OpenSSF Scorecard: what they ask of the contribution surface, and where this repository stands

- **Ticket**: #1549, "What do CNCF Sandbox, OpenSSF Best Practices at passing and Scorecard ask of a project's contribution surface, and where does this repository stand?", a child of wayfinder map #1548 (outside contribution, CNCF Sandbox-level hygiene).
- **Date**: 2026-11-25
- **Branch**: `research/cncf-hygiene`, based on origin/main at `feafd88c`.
- **Method**: read the primary sources in "Sources" (the CNCF charter, `cncf/foundation` DCO guidelines, the `cncf/sandbox` application form and onboarding checklist, the `cncf/toc` Incubation and Graduation application templates, `cncf/project-template`, the Linux Foundation generative-AI policy, the Linux kernel's coding-assistants document and its git history, the OpenSSF Best Practices passing criteria, and Scorecard's `docs/checks.md` and the scorecard-action README). Raw files on GitHub were read through `gh api` with the raw media type; other pages through a web fetch. The repository's state was read from origin/main and from the GitHub API. **Scorecard was not run**: running it in Docker needs a GitHub token passed into the container, and that step was refused in this session; no published Scorecard result exists for the repository (api.scorecard.dev returned 404). Scorecard rows below are therefore expectations from `checks.md` against the repository's observed state, not scores. No Best Practices badge entry was made.

## Answer in brief

1. **Claim 1 holds.** The CNCF charter, section 11(b)(ii), makes a DCO sign-off mandatory for every new inbound code contribution; 11(b)(i) leaves a CLA to each project. "DCO is the norm and no CLA is needed" is right; more exactly, DCO is required and a CLA is optional.
2. **Claim 2 is wrong on the level.** Multi-organization maintainers are a **Graduation** requirement, not an Incubation one. The Incubation template asks only for "a number of active maintainers which is appropriate to the size and scope of the project" and current affiliations. The Graduation template requires "maintainers from at least 2 organizations", an org-balance mechanism, and a contributor ladder producing maintainers from more than one organization. The map's conclusion (not needed now) survives; its wording "an Incubation requirement" does not.
3. **Claim 3 is half wrong.** The Linux Foundation generative-AI policy does cover AI-assisted contributions (they "can be contributed", subject to tool terms and third-party notices). It names **no** trailer and no declaration mechanism. `Assisted-by:` was a Linux kernel convention only, from January 2026, and the kernel **removed it** on 2026-10-14 (commit `1e035297ffda`, "docs: remove Assisted-by references"), moving disclosure to the cover letter. It is not a recognized LF or CNCF declaration; the project may still choose it as its own convention, but cannot cite the LF policy or the kernel for it.
4. **Sandbox asks** for an Apache-2.0 licence, a contributing guide, a code of conduct, a MAINTAINERS file with name, ID and organization, a security policy, a roadmap, and (at onboarding) the DCO app, the CNCF CoC referenced from the README, GOVERNANCE.md, SECURITY.md and a start on the Best Practices badge. The badge at passing is required from Incubation. This repository has the licence and the roadmap; it lacks every other file, DCO sign-off, issue and PR templates, and Discussions.
5. **Best Practices at passing** turns on the contribution surface through `interact`, `contribution`, `report_process`, `vulnerability_report_process`, `vulnerability_report_private`, `vulnerability_report_response` (14 days) and `test_policy`. The repository meets the quality and analysis criteria in substance (CI tests, warnings as errors, CodeQL, secret scanning). It misses the documented contribution process, the published vulnerability process, and a written test policy for contributors; release criteria are open because there are no releases.
6. **Scorecard**, from its documented rules, would score well on Token-Permissions, SAST, CI-Tests, License, Dangerous-Workflow and Binary-Artifacts; poorly on Code-Review (self-merged PRs with no approving review), Branch-Protection above tier 1 (no required reviewers, so the status-check tier does not count), Security-Policy (no SECURITY.md), Dependency-Update-Tool (no Dependabot or Renovate config), Pinned-Dependencies (actions pinned by tag, not SHA), CII-Best-Practices (no badge) and Contributors; Maintained is not assessed until the repository is 90 days old (first commit 2026-10-12).

## 1. The three charting claims

### Claim 1: DCO sign-off is the CNCF norm, and no CLA is needed

**Holds.** CNCF charter, section 11 "IP Policy":

- (b)(ii): "All new inbound code contributions to the CNCF shall be (i) accompanied by a Developer Certificate of Origin sign-off (https://developercertificate.org) and (ii) made under the Apache License, Version 2.0".
- (b)(i): "Each project shall determine whether it will require use of an approved CNCF CLA."

The Sandbox onboarding checklist repeats it: "Ensure the DCO app is enabled for all GitHub repositories of the project. You may also choose to use a CLA in addition to the DCO." The CNCF DCO Guidelines v1.0 add that the DCO "requires the use of a real name", which "does not require a legal name", and that it should "not be an anonymous id or false name". They say nothing about bot or agent commits.

Bearing on this repository: the kernel's coding-assistants document (still current on this point) says "AI agents MUST NOT add Signed-off-by tags" and "Only humans can legally certify the Developer Certificate of Origin". That matches the map's rule that the contributor signs off; for this repository's own agent-authored commits it means the human's sign-off, not the agent's, certifies the DCO. None of the last 500 commits on origin/main carries `Signed-off-by:`.

### Claim 2: multi-org maintainers are required only from Incubation

**Wrong on the level.** From `cncf/toc` `.github/ISSUE_TEMPLATE/template-incubation-application.md` (v1.6):

- "A number of active maintainers which is appropriate to the size and scope of the project."
- "Document complete list of current maintainers, including names, contact information, domain of responsibility, and affiliation."

No Incubation criterion asks for more than one organization. From `template-graduation-application.md`:

- "Project maintainers from at least 2 organizations that demonstrates survivability."
- "Document and demonstrate an org-balance mechanism for governance decisions".
- "Contributor ladder with multiple roles for contributors, with demonstrated evidence of the ladder producing maintainers from more than one contributing organization, as applicable."

At Sandbox the application form says organizational diversity is "not a requirement for Sandbox, but the TOC does consider it during review." So a single maintainer is acceptable at Sandbox and at Incubation (if appropriate to the project's size); multi-org is a Graduation requirement. The map's Out-of-scope line "Multi-org maintainers, an Incubation requirement" should read "a Graduation requirement".

### Claim 3: the LF generative-AI policy covers AI-assisted contributions, and `Assisted-by:` is a recognized declaration

**First half holds; second half is wrong.**

The Linux Foundation's "Guidance Regarding Use of Generative AI Tools for Open Source Software Development" says "Code or other content generated in whole or in part using AI tools can be contributed to Linux Foundation projects." It asks contributors to check that the tool's terms do not restrict the output in ways inconsistent with the project's licence or IP policy, and, where third-party material appears in the output, to confirm permission and "provide notice and attribution of such third party rights". It lets projects add their own guidance. It names no commit trailer and no way of declaring AI assistance.

`Assisted-by:` came from the Linux kernel, one LF-hosted project, not from the LF:

- 2026-01-06, `78d979db6cef` "docs: add AI Coding Assistants documentation": `Assisted-by: AGENT_NAME:MODEL_VERSION [TOOL1] [TOOL2]` (still visible in the v7.1 and v7.2 rendered docs).
- 2026-08-03, `816d9992d9ed` "coding-assistants: simplify attribution": reduced to `Assisted-by: LLM`.
- 2026-10-14, `1e035297ffda` "docs: remove Assisted-by references": removed the tag from `coding-assistants.rst`, `submitting-patches.rst` and checkpatch. Its message: about 10% of 7.3's commits carried the tag "while actual LLM use is far more widespread", so "the tag does not provide the signal it was kept for"; disclosure "belongs in the cover letter". `generated-content.rst`'s transparency request is unchanged.

The current kernel docs (`docs.kernel.org/process/coding-assistants.html`) have no Attribution section. No CNCF policy on AI contributions was found in `cncf/foundation/policies-guidance`.

So the map's preference "AI-assisted PRs are welcome under the Linux Foundation's generative-AI policy" stands. "Declares assistance with an `Assisted-by:` trailer" is the project's own choice with no foundation behind it, and the one project that defined the trailer has just abandoned it as unenforceable. The repository's own commits declare agent work with `Co-Authored-By:` (395 of the last 500 commits on origin/main), not `Assisted-by:` (none).

## 2. CNCF Sandbox: what it asks

The `cncf/sandbox` form (`.github/ISSUE_TEMPLATE/application.yml`) requires URLs for the contributing guide, code of conduct, MAINTAINERS file and security policy file, plus a roadmap and a website. The MAINTAINERS file must be a direct link to a table with Name, ID and Company/Organization columns. The licence "Must use Apache 2.0". The pre-submission checklist asks for a "Repository is 6+ months old with active development." The onboarding checklist (`project-onboarding.md`) adds: the DCO app on every repository, the CNCF CoC "explicitly referenced in the project's README.md", start GOVERNANCE.md, start SECURITY.md, and "Start working on an OpenSSF Best Practices Badge." `cncf/project-template` lists as required for all CNCF projects LICENSE, README, CONTRIBUTING.md, CODE_OF_CONDUCT.md, MAINTAINERS.md, GOVERNANCE.md and SECURITY.md, and as recommended ADOPTERS.md, CONTRIBUTOR_LADDER.md, RELEASES.md, REVIEWING.md, CODEOWNERS and issue and PR templates. The Incubation template requires the Best Practices passing badge.

## 3. OpenSSF Best Practices at passing: the contribution surface

From `bestpractices.dev/en/criteria/0`: `interact` (the site says how to obtain, give feedback and contribute), `contribution` ("MUST explain the contribution process"), `contribution_requirements` (SHOULD state the requirements for acceptable contributions), `report_process`, `report_responses` (acknowledge a majority of bug reports in the last 2 to 12 months), `report_archive`, `vulnerability_report_process` ("MUST publish the process for reporting vulnerabilities on the project site"), `vulnerability_report_private`, `vulnerability_report_response` (initial response within 14 days), `test_policy` and `tests_documented_added` (the test policy in the change-proposal instructions), plus the quality and analysis criteria (`test`, `test_continuous_integration`, `warnings`, `static_analysis`). No passing criterion concerns DCO or sign-off. Whether a project with no releases may mark `release_notes` and `version_unique` N/A is not stated on the criteria page; it is open until the badge form is filled.

## 4. OpenSSF Scorecard: the contribution surface

From `ossf/scorecard` `docs/checks.md`. Code-Review counts a change as reviewed on an approving review, or when the merger differs from the committer; bot reviews, AI bots included, do not count. Branch-Protection is tiered: tier 1 no force push or deletion (3/10), tier 2 at least one required reviewer (6/10), tier 3 a required status check (8/10), tier 4 two reviewers and code-owner review (9/10), tier 5 dismiss stale reviews and include administrators (10/10); a tier must be met before the next counts. Security-Policy scores a SECURITY.md with a reporting link or address (6), free text (3) and wording on "vuln", "disclos" and a time expectation (1). Pinned-Dependencies wants actions pinned to a full commit SHA (an immutable-release tag also counts) and Dockerfile bases pinned by digest. Dependency-Update-Tool counts Dependabot and Renovate only. Contributors counts the GitHub profile Company of recent committers. Maintained is not assessed for a repository younger than 90 days. Publishing results needs the scorecard-action workflow with `security-events: write` and `id-token: write` on the scorecard job.

## 5. Gap table

State is origin/main at `feafd88c` and the GitHub settings read on 2026-11-25.

| Requirement | Source | This repository's state | Gap |
| --- | --- | --- | --- |
| Apache-2.0 licence in a standard location | charter 11(b); sandbox form; BP `floss_license`, `license_location`; SC License | `LICENSE` is Apache-2.0; GitHub detects it | None |
| DCO sign-off on every inbound code contribution, DCO app enabled | charter 11(b)(ii); sandbox onboarding | No `Signed-off-by:` in the last 500 commits; no DCO check in CI or on GitHub | Sign-off rule, CI or app check, and a decision on agent-authored commits (the human signs off) |
| CLA | charter 11(b)(i): optional | None | None (the map's "no CLA" is allowed) |
| Contributing guide; contribution process explained | sandbox form; project-template; BP `interact`, `contribution`, `contribution_requirements` | No CONTRIBUTING.md; README does not say how to contribute; rules live in AGENTS.md and `docs/agents/` | CONTRIBUTING.md |
| Code of conduct, CNCF CoC referenced from README | sandbox form and onboarding; project-template | None; GitHub community profile shows no CoC | CODE_OF_CONDUCT.md and a README reference |
| MAINTAINERS file: Name, ID, Company/Organization table | sandbox form; project-template; Incubation template (affiliation) | None | MAINTAINERS.md |
| Maintainers from at least 2 organizations | Graduation template only; Sandbox "not a requirement" | One maintainer | None at Sandbox or Incubation (claim 2 corrected) |
| Governance document | sandbox onboarding; project-template | None | GOVERNANCE.md |
| Contributor ladder | project-template (recommended); Graduation (multi-org evidence) | None | CONTRIBUTOR_LADDER.md (recommended, and in the map's Notes) |
| Security policy with private reporting route and a response time | sandbox form; BP `vulnerability_report_process`, `_private`, `_response` (14 days); SC Security-Policy | GitHub private vulnerability reporting is enabled; no SECURITY.md | SECURITY.md naming the private route and an acknowledgement time no longer than 14 days |
| Bug report process and public archive | BP `report_process`, `report_archive`, `report_tracker` | Issues enabled and public; new outside issues labelled `needs-triage` (#1547) | No issue templates; the process is not written for an outsider |
| Searchable discussion channel | BP `discussion` | Discussions disabled | Enable Discussions (the map's choice) |
| Issue and PR templates | project-template (recommended) | None | Templates |
| `good first issue` and `help wanted` labels | map's choice (CNCF-wide convention) | Neither label exists | Labels |
| Roadmap | sandbox form | `docs/roadmap.md` | None |
| Adopters list | project-template (recommended); "required for moving levels" | None | ADOPTERS.md stub (in the map's Not yet specified) |
| Repository 6+ months old | sandbox pre-submission checklist | First commit 2026-10-12 | Time only; the application is out of scope |
| Best Practices badge at passing | sandbox onboarding ("start"); Incubation and Graduation (required); SC CII-Best-Practices | No bestpractices.dev entry | Register and complete the passing form |
| Written test policy for new functionality | BP `test_policy`, `tests_documented_added` | Tests are required in practice (`CODING_STANDARDS.md`, test ids per plan row) but not written for outside contributors | One line in CONTRIBUTING.md pointing at the rule's home |
| Automated tests run in CI on changes | BP `test`, `test_continuous_integration`; SC CI-Tests | CI runs on `pull_request` and push to main; four required checks | None |
| Warnings enabled and fixed | BP `warnings`, `warnings_fixed`, `warnings_strict` | Warnings are errors; analyzers at `10-all` | None |
| Static analysis | BP `static_analysis`, `static_analysis_often`; SC SAST | CodeQL on PRs and weekly, a required check | None |
| Release notes, unique versions, version tags | BP `release_notes`, `version_unique`, `version_tags`; SC Signed-Releases, Packaging | No GitHub releases; gate tags only | Open: N/A or a release process (the map's Not yet specified) |
| Pull requests reviewed before merge | SC Code-Review | Recent PRs authored and merged by the same account with no approving review; review records are comments | Scorecard will score this low with one maintainer; record it, or require an approval once a second reviewer exists |
| Branch protection beyond status checks | SC Branch-Protection | No force push, no deletion, four required checks; no required reviewers; admins not enforced | Tier 2 (a required reviewer) is not met, so tiers 3 to 5 do not count; record as a known finding with one maintainer |
| Top-level workflow token read-only | SC Token-Permissions | `ci.yml`, `codeql-weekly.yml`, `startup-bench.yml` top-level `contents: read`; `issue-labels.yml` and `ticket-shape.yml` grant `issues: write` at the top level | Move `issues: write` to the job level |
| Dependencies pinned | SC Pinned-Dependencies | Dockerfile bases pinned by digest; actions pinned by major tag (`actions/checkout@v4`); no NuGet lock files | Pin actions by SHA; consider lock files |
| Dependency update tool | SC Dependency-Update-Tool | No Dependabot or Renovate config; Dependabot security updates disabled | Dependabot or Renovate config, or a recorded finding |
| No dangerous workflow patterns | SC Dangerous-Workflow | No `pull_request_target` or `workflow_run` | None; keep it so when fork PRs arrive |
| No committed binaries | SC Binary-Artifacts | None found | None |
| Leaked credentials | BP `no_leaked_credentials` | Secret scanning on; push protection off | Consider push protection |
| Webhooks have secrets | SC Webhooks | No webhooks | None |
| Fuzzing | SC Fuzzing | None (FsCheck would count for C#) | Recorded finding, or a property test |
| Publish Scorecard results | scorecard-action README | No scorecard workflow; no published result | A scorecard workflow if the badge is wanted |
| Signed commits | SC (none); CNCF (none) | GitHub-made merges verified; local commits unsigned | None required |
| AI-assistance declaration | LF policy (none named); kernel (removed 2026-10-14) | Agent commits carry `Co-Authored-By:` | A project choice; `Assisted-by:` has no foundation authority (claim 3 corrected) |

## Executed and read

**Executed** (2026-11-25):

- `git fetch origin --tags`; `git log`, `grep` and `find` over origin/main at `feafd88c` (files present, workflow `permissions` and `uses:` lines, Dockerfile `FROM` lines, trailer counts over the last 500 commits).
- `gh api` against `repos/adammedford/spa-platform`: repository settings and security-and-analysis, `branches/main/protection`, `rulesets`, `private-vulnerability-reporting`, `community/profile`, `hooks`, commit verification for the last 30 commits; `gh pr list --state merged`; `gh label list`; `gh release list`.
- Not executed: OpenSSF Scorecard (the Docker run with a GitHub token was refused in this session); no Best Practices badge entry.

**Read** (all on 2026-11-25):

- CNCF charter, section 11: https://github.com/cncf/foundation/blob/main/charter.md
- CNCF DCO Guidelines v1.0: https://github.com/cncf/foundation/blob/main/policies-guidance/dco-guidelines.md
- CNCF policies-guidance listing (no AI policy found): https://github.com/cncf/foundation/tree/main/policies-guidance
- Sandbox README, application form and onboarding checklist: https://github.com/cncf/sandbox, https://github.com/cncf/sandbox/blob/main/.github/ISSUE_TEMPLATE/application.yml, https://github.com/cncf/sandbox/blob/main/.github/ISSUE_TEMPLATE/project-onboarding.md
- TOC graduation criteria pointer and application templates: https://github.com/cncf/toc/blob/main/process/graduation_criteria.md, https://github.com/cncf/toc/blob/main/.github/ISSUE_TEMPLATE/template-incubation-application.md, https://github.com/cncf/toc/blob/main/.github/ISSUE_TEMPLATE/template-graduation-application.md
- CNCF project template: https://github.com/cncf/project-template
- Linux Foundation generative-AI policy: https://www.linuxfoundation.org/legal/generative-ai
- Linux kernel AI coding assistants, current and v7.1/v7.2: https://docs.kernel.org/process/coding-assistants.html, https://docs.kernel.org/7.1/_sources/process/coding-assistants.rst.txt, https://docs.kernel.org/7.2/process/coding-assistants.html; tool-generated content: https://docs.kernel.org/process/generated-content.html
- Kernel commits on `Documentation/process/coding-assistants.rst` (`78d979db6cef`, `816d9992d9ed`, `1e035297ffda`): https://github.com/torvalds/linux/commits/master/Documentation/process/coding-assistants.rst
- Kernel mailing-list thread on dropping the attribution requirement, July 2026: https://lkml.iu.edu/2607.0/13046.html
- OpenSSF Best Practices passing criteria: https://www.bestpractices.dev/en/criteria/0
- OpenSSF Scorecard checks: https://github.com/ossf/scorecard/blob/main/docs/checks.md; scorecard-action README: https://github.com/ossf/scorecard-action
- Scorecard API for this repository (404, no published result): https://api.scorecard.dev/projects/github.com/adammedford/spa-platform
