# What GitHub does with CODEOWNERS it cannot resolve

Findings for ticket `.scratch/g1a-and-landing-work/issues/02-what-github-does-with-codeowners-it-cannot-resolve.md`.

All web sources retrieved **2026-10-12**. Where a GitHub Docs page is gated by a plan note, the note
is rendered from a reusable in the public `github/docs` repository; the reusable's own last-changed
date is given, because that dates the current state of the policy rather than the page.

This file records facts only. The decision belongs to ticket 03.

---

## 1. What happens to an unresolvable owner

**Answer.** Neither a hard error nor a silent ignore: GitHub parses the line, fails to resolve the
owner, assigns nobody, and records a per-line error of kind `Unknown owner`. It annotates; it blocks
nothing by itself. Two surfaces report it: the CODEOWNERS file view in the repository UI, which
highlights errors, and the REST endpoint `GET /repos/{owner}/{repo}/codeowners/errors`. A bad owner
on one line does not invalidate the rest of the file.

**Documented.** From *About code owners*
(<https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners>,
page source last changed 2026-08-14):

> If any line in your CODEOWNERS file contains invalid syntax, that line will be skipped. When you
> navigate to the CODEOWNERS file in your repository, you can see any errors highlighted. A list of
> errors in a repository's CODEOWNERS file is also accessible via the API.

> If you specify a user or team that doesn't exist or has insufficient access, a code owner will not
> be assigned.

> The people you choose as code owners must have write permissions for the repository. When the code
> owner is a team, that team must be visible and it must have write permissions, even if all the
> individual members of the team already have write permissions directly, through organization
> membership, or through another team membership.

The endpoint is documented as *List CODEOWNERS errors*, "List any syntax errors that are detected in
the CODEOWNERS file", with a response array of objects carrying `line`, `column`, `source`, `kind`,
`suggestion`, `message`, `path`
(<https://docs.github.com/en/rest/repos/repos?apiVersion=2022-11-28#list-codeowners-errors>). The
docs give no example error messages and state no permission requirement for the endpoint.

**Observed.** The exact case in question — a `@org/team` handle in a repository that is not in that
org — reproduces on public personal-account forks of `github/docs`. That repository's
`.github/CODEOWNERS` is four lines, with one owner line: `content/site-policy/ @github/site-policy-admins`.
Queried 2026-10-12 with `gh api` as `adammedford`:

- `gh api /repos/github/docs/codeowners/errors` → `{"errors":[]}`
- `gh api /repos/travisfortney/docs/codeowners/errors` (and the same for `akujon720-dot/docs` and
  `kennethgee247-cyber/docs`, all personal forks) → identical single error:

```json
{"errors":[{"line":4,"column":22,
 "source":"content/site-policy/ @github/site-policy-admins\n",
 "kind":"Unknown owner",
 "suggestion":"make sure the team @github/site-policy-admins exists, is publicly visible, and has write access to the repository",
 "message":"Unknown owner on line 4: make sure the team @github/site-policy-admins exists, is publicly visible, and has write access to the repository\n\n  content/site-policy/ @github/site-policy-admins\n                       ^",
 "path":".github/CODEOWNERS"}]}
```

**Where documentation and behaviour differ.** The REST reference calls these "syntax errors". The
observed `kind` is `Unknown owner` — a resolution failure, not a syntax failure. So the endpoint
reports more than the docs claim, and an unresolvable handle *is* reported rather than skipped
silently. The docs' separate sentence about invalid syntax being "skipped" is about malformed lines,
which is a different case from an unresolvable owner.

**Unestablished.** The exact wording and shape of the in-UI annotation (banner, count, per-line
marker) was not observed, because that requires loading the file view in a browser session on a
repository; only the API surface was observed. Whether the same errors are computed and surfaced on
a *private* repository, and on a private repository whose plan does not include code owners (see §2),
was not observed.

---

## 2. What a personal repository supports

**Answer.** CODEOWNERS is evaluated on personal repositories — observed above, on public personal
forks. But code owners is a **plan-gated feature**: on a **private** repository it requires GitHub
Pro or better. GitHub Free covers public repositories only. Within a personal repository, only user
handles (`@username`) or the email of a user account can ever resolve; `@org/team-name` handles
cannot, because teams exist only in organizations. A user handle resolves only if that user is a
collaborator with **explicit write access** to the repository.

**Documented.** The plan note rendered at the top of *About code owners* (retrieved 2026-10-12;
source `data/reusables/gated-features/code-owners.md`, last changed 2024-02-06):

> You can define code owners in public repositories with GitHub Free and GitHub Free for
> organizations, and in public and private repositories with GitHub Pro, GitHub Team, GitHub
> Enterprise Cloud, and GitHub Enterprise Server.

*GitHub's plans* (<https://docs.github.com/en/get-started/learning-about-github/githubs-plans>, page
source last changed 2026-09-15) lists under **GitHub Pro**, as "Advanced tools and insights in
private repositories": "Required pull request reviewers", "Multiple pull request reviewers",
"Protected branches", "Code owners", "Auto-linked references". None of those four appear in the
GitHub Free for personal accounts list, which describes "unlimited private repositories with a
limited feature set".

On owner syntax and access, from *About code owners*:

> The pattern is followed by one or more GitHub usernames or team names using the standard
> `@username` or `@org/team-name` format. Users and teams must have explicit `write` access to the
> repository, even if the team's members already have access.

> When using teams, only organization teams can be code owners. Enterprise teams are not supported
> as code owners.

**Consequence for this repository.** Every handle in `/Users/adammedford/Documents/Projects/spa-platform/CODEOWNERS`
is a team handle of the form `@spa-platform/<role>`. Counted on 2026-10-12: **26 rule lines carrying
7 distinct handles** — `integrator`, `ws1-infra`, `ws2-hosting`, `ws3-identity`, `ws4-proxy`,
`ws5-fixtures`, `ws6-verification`. (The ticket says "nine"; the file has seven distinct handles. The
count does not change any conclusion here, but it is worth correcting in ticket 03.) On a
personal-account repository none of them can resolve under any plan: the
organization does not exist, and a personal repository has no teams to point them at. Every rule
would carry an `Unknown owner` error, and no reviewer would ever be assigned.

**Unestablished.** Whether, on a **private personal repository on GitHub Free**, the CODEOWNERS file
is ignored entirely (no parsing, no error list, no shield icon) or parsed with errors shown while
assignment is disabled. The gating note says the feature is unavailable; it does not say which of
those two shapes "unavailable" takes. Ticket 01 can observe this directly once the repository exists.

---

## 3. Whether an unresolvable entry weakens anything we rely on

**Answer.** Inert, as far as can be established. Nothing in branch protection or rulesets reads
CODEOWNERS except one optional setting, "Require review from Code Owners". Required status checks,
the required number of approving reviews, force-push and deletion blocks, linear history, signed
commits and conversation resolution are all configured independently and are not affected by a
CODEOWNERS file that resolves to nobody. A-05 is likewise unaffected (below).

**Documented.** From *About protected branches*
(<https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches>):

> Optionally, you can choose to require reviews from code owners. If you do, any pull request that
> affects code with a code owner must be approved by that code owner before the pull request can be
> merged into the protected branch.

> Required status checks must have a `successful`, `skipped`, or `neutral` status before collaborators
> can make changes to a protected branch. Required status checks can be checks or commit statuses.

From *About code owners*: "When reviews from code owners are required, an approval from any of the
owners is sufficient to meet this requirement."

**The one coupling, and the limit of what is established.** Chaining the two documented statements —
"a code owner will not be assigned" for an unresolvable handle, and the requirement applies to "code
with a code owner" — implies that for a path whose only owner is unresolvable there is nothing for
the code-owner-review requirement to require, so it neither blocks the merge nor is satisfied by
anyone. **This is an inference from two documented sentences, not a documented statement, and it was
not observed.** GitHub Docs nowhere state what happens when "Require review from Code Owners" is
enabled and no owner resolves for a changed path. The alternative outcome — an unsatisfiable "Review
required" that blocks merging — is asserted in community threads but I found no primary source for
it either way. Treat the question "can a merge be blocked by an unresolvable owner under required
code-owner review?" as **unestablished**.

A related documented failure mode, worth recording because its blast radius is the whole file rather
than one line: "CODEOWNERS files must be under 3 MB in size. A CODEOWNERS file over this limit will
not be loaded, which means that code owner information is not shown and the appropriate code owners
will not be requested to review changes in a pull request." The repository's file is ~2 KB, so this
does not apply.

**A-05 confirmed independent of resolvability.** Read directly from
`/Users/adammedford/Documents/Projects/spa-platform/.github/path-ownership.py`:

- `rules()` reads `REPOSITORY_ROOT / "CODEOWNERS"` as text, drops everything from the first `#`,
  splits on whitespace, takes field 0 as the pattern and the **first** remaining token as the owner,
  then reduces it with `owners[0].rsplit("/", 1)[-1]` — so `@spa-platform/ws2-hosting` becomes the
  string `ws2-hosting`.
- The script makes no network call. Its only subprocess calls are `git merge-base`, `git diff
  --name-only` and `git log`. Nothing in it consults GitHub, so whether a handle resolves to a real
  account is irrelevant to the check. The ticket's assumption holds.

Two mechanical facts that follow, stated without recommendation because ticket 03 decides:

- Because `rules()` strips everything after `#`, commenting out the owner lines would leave A-05 with
  **no rules at all**. Every changed path would then hit the `unowned` branch and the check would
  fail with "A-05 FAILED: no owner for:" — its own comment notes this means "the catch-all is gone".
- A-05 reads the **root** `CODEOWNERS` only. GitHub reads `.github/CODEOWNERS`, then root
  `CODEOWNERS`, then `docs/CODEOWNERS`, and "will search for them in that order and use the first one
  it finds" (*About code owners*). The two readers can therefore be pointed at different files, and
  GitHub additionally requires its copy to be on the pull request's **base** branch to trigger review
  requests.

---

## 4. Whether a comment-only ownership record is a recognised pattern

**Answer.** GitHub documents no such pattern — there is no GitHub-published guidance on recording
intended ownership in a form GitHub will not try to resolve. What GitHub does document is the
inverse, and precisely: the exact paths it reads. A file at any other path or under any other name is
not an input to the feature at all — nothing is parsed, nothing is resolved, no error list is
produced and no review is assigned. Outside GitHub's documentation the general pattern is well
established: Kubernetes/Prow `OWNERS` files are per-directory ownership records that GitHub does not
read and that external CI enforces.

**Documented (what GitHub reads).** From *About code owners*:

> To use a CODEOWNERS file, create a new file called `CODEOWNERS` in the `.github/`, root, or `docs/`
> directory of the repository, in the branch where you'd like to add the code owners. If `CODEOWNERS`
> files exist in more than one of those locations, GitHub will search for them in that order and use
> the first one it finds.

Note that `docs/` is in that list, but only the file named exactly `CODEOWNERS` in it. Comments inside
a CODEOWNERS file are documented and ignored by GitHub; the page's own example opens with
`# This is a comment.` and `# Each line is a file pattern followed by one or more owners.` The page
also warns that gitignore escaping of a leading `#` does **not** work in CODEOWNERS, so a `#` line is
always a comment.

**Recognised precedent elsewhere (not GitHub).** `kubernetes/community`,
`contributors/guide/owners.md` (<https://github.com/kubernetes/community/blob/master/contributors/guide/owners.md>,
read via the API 2026-10-12):

> OWNERS files are used to designate responsibility over different parts of the Kubernetes codebase.

> Our OWNERS files were inspired by Chromium OWNERS files which in turn inspired GitHub's CODEOWNERS
> files.

> The k8s.io/test-infra/prow/repoowners package is the main consumer of OWNERS files.

That is the nearest documented precedent for the shape in question: ownership recorded in a file the
forge does not resolve, enforced by a tool in CI. It is a third-party convention, documented by
Kubernetes, not by GitHub.

**Unestablished.** Whether GitHub itself anywhere endorses or names a "record ownership without
resolution" pattern. I found none; absence of evidence here, but I searched the CODEOWNERS pages and
their reusables.

---

## 5. Branch protection on a private personal repository: which plan

**Answer.** **GitHub Pro** is the minimum for a private repository under a personal account, as of
2026-10-12 — for protected branches (which is where required status checks and required reviewers
live), for rulesets, and for code owners alike. GitHub Free gives all three in **public**
repositories only. There is no separate gate for required status checks: it is a setting inside a
branch protection rule or a ruleset and inherits that gate. Some branch-protection sub-features stay
out of reach on a personal account at **any** plan, because they require an organization.

**Documented — protected branches.** Plan note rendered at the top of *About protected branches*
(retrieved 2026-10-12; source `data/reusables/gated-features/protected-branches.md`, last changed
2025-02-28):

> Protected branches are available in public repositories with GitHub Free and GitHub Free for
> organizations. Protected branches are also available in public and private repositories with GitHub
> Pro, GitHub Team, GitHub Enterprise Cloud, and GitHub Enterprise Server.

**Documented — rulesets.** Plan note on *About rulesets*
(<https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets>;
source `data/reusables/gated-features/repo-rules.md`, last changed 2026-06-09):

> Rulesets are available in public repositories with GitHub Free and GitHub Free for organizations,
> and in public and private repositories with GitHub Pro, GitHub Team, and GitHub Enterprise Cloud.

> Push rulesets are available for the GitHub Team plan in internal and private repositories, and
> forks of repositories that have push rulesets enabled.

So: rulesets on a private personal repository need Pro; *push* rulesets need Team, i.e. an
organization, and are out of reach on a personal account. Organization-wide rulesets need Enterprise.

**Documented — the plans page.** *GitHub's plans* (page source last changed 2026-09-15) lists under
**GitHub Pro**, "Advanced tools and insights in private repositories": Required pull request
reviewers, Multiple pull request reviewers, **Protected branches**, **Code owners**, Auto-linked
references.

**Organization-only, at any plan.** From *About protected branches*:

> Actors may only be added to bypass lists when the repository belongs to an organization.

> You can enable branch restrictions in public repositories owned by a GitHub Free organization and
> in all repositories owned by an organization using GitHub Team or GitHub Enterprise Cloud.

That second sentence governs "Restrict who can push to matching branches", which therefore cannot be
used on a personal repository at all. Ruleset bypass lists are subject to the same organization
requirement.

**Historical direction, for the "pricing has changed" concern.** The gating text above is the current
text and names Pro for private repositories. The only movement I could date is in the *rulesets*
reusable (last touched 2026-06-09 for an unrelated GHES deprecation); the *protected branches*
reusable has not changed since 2025-02-28. I did **not** find a changelog entry making branch
protection or rulesets free on private repositories for personal accounts, and the current docs
contradict any such change.

**Commercial detail, dated.** On 2026-10-12, <https://github.com/pricing> shows a three-way
comparison — Free ($0/month forever), Team ($4 USD per user/month for the first 12 months) and
Enterprise ($21 USD per user/month for the first 12 months) — and **does not list GitHub Pro or its
price at all**; its feature table marks "Code owners" and "Repository rules" with a "Public
repositories" qualifier under Free. The docs plans page, updated 2026-09-15, still lists GitHub Pro
as a current individual plan. The only GitHub-published figure I could find for Pro is on
*FAQ about changes to GitHub's plans*
(<https://docs.github.com/en/get-started/learning-about-github/faq-about-changes-to-githubs-plans>):
"GitHub Pro now has a reduced monthly price of $4" — a page about the April 2020 plan changes, so not
usable as a current price.

**Unestablished.**

- **The current price of GitHub Pro.** Docs still name the plan; the public pricing page no longer
  lists it or any figure for it. The $4/month figure has only a 2020-vintage source. Checking the
  upgrade flow in account settings (authenticated) would settle it; I did not do that.
- **What GitHub does when the plan does not cover the feature.** Whether creating a branch protection
  rule or a ruleset on a Free private personal repository is refused outright (e.g. HTTP 403 on the
  API, a disabled form), or accepted and silently not enforced, is not stated in the docs I read and
  was not observed. Community threads report both shapes. Ticket 01 tries it directly and should be
  treated as the authority on observed behaviour.

---

## Summary of what could not be established

1. The in-UI wording and shape of the CODEOWNERS error annotation (API surface observed; UI not).
2. Whether a private repository, and a private repository on a plan without code owners, still
   computes and exposes CODEOWNERS errors at all.
3. Whether "Require review from Code Owners" can block a merge when no owner resolves for a changed
   path. Documented statements imply it cannot; neither the docs nor an observation confirm it.
4. Whether GitHub itself documents any "ownership record GitHub does not resolve" pattern. Found
   none.
5. The current price of GitHub Pro, and whether a Free private personal repository refuses branch
   protection and rulesets outright or accepts them unenforced.

## Reproducing the observations

```bash
gh api /repos/github/docs/codeowners/errors                 # {"errors":[]}
gh api /repos/travisfortney/docs/codeowners/errors           # one "Unknown owner" error
gh api /repos/travisfortney/docs/contents/.github/CODEOWNERS --jq .content | base64 -d

# the plan notes, from the docs source rather than the rendered page
curl -s https://raw.githubusercontent.com/github/docs/main/data/reusables/gated-features/protected-branches.md
curl -s https://raw.githubusercontent.com/github/docs/main/data/reusables/gated-features/repo-rules.md
curl -s https://raw.githubusercontent.com/github/docs/main/data/reusables/gated-features/code-owners.md
```
