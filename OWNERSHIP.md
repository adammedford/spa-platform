# Path ownership

One owner per path. The last matching line wins, which is git's own rule.

The owners are the roles in the plan (`docs/plan/03-workstreams-and-tasks.md`). They are **role
names, not GitHub handles**: this repository has one author, so handle resolution would be theatre.
`.github/path-ownership.py` reads the block below as text and nothing else consumes it.

Check A-05 runs in `.githooks/pre-push`. A linear branch must touch one owner's set across its
whole diff. A history containing a merge must keep each ordinary commit and each merge's resolution
edits within one owner's set; imported parent commits are audited separately, including reverted
commits. Two-parent merges are supported; octopus merges fail with instructions to split them.
When it fires, look at what it names: either the change reached somewhere it did not mean to,
or it needs an integrator path and wants a change request under `contracts/changes/`. Anyone may add
one of those, which is why the block exempts it.

```text
*                                     integrator

# 1 AWS and IaC
/infra/                               ws1-infra
/tools/scripts/                       ws1-infra

# 2 Hosting and registry
/src/Platform.Registry/               ws2-hosting
/src/Platform.Hosting/                ws2-hosting
/tools/spactl/                        ws2-hosting
/tests/Platform.Registry.Tests/       ws2-hosting
/tests/Platform.Hosting.Tests/        ws2-hosting
/tests/Spactl.Tests/                  ws2-hosting

# 3 OAuth and sessions
/src/Platform.Identity/               ws3-identity
/tests/Platform.Identity.Tests/       ws3-identity
/spikes/g0/                           ws3-identity

# 4 API and security
/src/Platform.Proxy/                  ws4-proxy
/src/Platform.Security/               ws4-proxy
/tests/Platform.Proxy.Tests/          ws4-proxy
/tests/Platform.Security.Tests/       ws4-proxy

# 5 Fixtures
/fixtures/                            ws5-fixtures

# 6 Verification and operations
/src/Platform.Telemetry/              ws6-verification
/tests/Platform.Telemetry.Tests/      ws6-verification
/tests/Gateway.TestHarness/           ws6-verification
/tests/Gateway.IntegrationTests/      ws6-verification
/tests/Gateway.SecurityTests/         ws6-verification
/tests/Gateway.ArchitectureTests/     ws6-verification
/tests/e2e/                           ws6-verification
/docs/runbooks/                       ws6-verification
/docs/gaps.md                         ws6-verification
```
