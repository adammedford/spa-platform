# 1.4a — The cell's ECS resources, written and planned

Status: ready-for-agent
Owner: workstream 1 (AWS and IaC)
Parent row: 1.4 (ECS — the service is steady on the sample image and its target is healthy)
Base: trunk

## Problem Statement

The cell has a network, an internal load balancer with a listener and a target group, a bucket and
two image repositories. It has nothing to run. The cell module's own header comment already says the
ECS cluster, task definition and service belong in it, and the load balancer's target group has no
target.

Row 1.4 is blocked, but not because the Terraform cannot be written. It is blocked because its
done-when is a running service with a healthy target, which needs credentials, an applied state
bucket and a human at the keyboard. Everything that can be decided and checked without AWS — the
resource set, the task shape, the environment the gateway reads, and above all the task role's
permissions — is sitting unwritten behind that apply.

That ordering costs twice. The applies that unblock the rest of the roadmap cannot even be attempted
until this Terraform exists, so the one sitting that moves the critical path is gated on work that
needs no sitting at all. And the task role, which is the most security-sensitive object in the cell,
would be written under time pressure during that sitting rather than reviewed beforehand.

## Solution

Write the cell's ECS resources and prove everything about them that a plan against a mock provider
can prove: the resource set, the task's shape and platform, the service's deployment behaviour, the
log group, the environment variables the platform configuration contract prescribes, and the task
role's permissions scoped to exactly this cell's bucket prefixes and parameter path.

No apply, no credentials, no network beyond `terraform init`. The row's own done-when — a steady
service on a healthy target — stays with row 1.4 and the AWS sitting. What this sub-row delivers is
that the sitting starts from reviewed, planned Terraform instead of a blank file.

## User Stories

1. As an operator, I want the cell's ECS cluster, task definition, service and log group defined in
   the cell module, so that one module instance is still the whole cell.
2. As an operator, I want the task to run on ARM64 at a quarter vCPU and a gigabyte, so that the
   cell costs what the plan says it costs.
3. As an operator, I want the service to register with the load balancer's existing target group, so
   that the listener that already exists finally has a target.
4. As an operator, I want the deployment circuit breaker on, so that a bad image rolls back instead
   of retrying forever.
5. As an operator, I want ECS Exec enabled, so that the Session Manager tunnel the verification
   scripts need has something to connect through.
6. As an operator, I want a log group with an explicit retention, so that logs neither vanish nor
   accumulate without a decision.
7. As an operator, I want the task to carry a public address in this cell's public subnets, so that
   the no-NAT network design still lets the task pull an image.
8. As a gateway developer, I want the task environment to follow the platform configuration
   contract, so that the process reads the settings it expects without a translation layer.
9. As a gateway developer, I want the contract's nested keys rendered as the double-underscore
   environment form, so that the configuration binder sees the shape the schema describes.
10. As a gateway developer, I want the registry bucket, key and poll interval supplied from the
    cell's own resources, so that no environment value is typed twice.
11. As a gateway developer, I want the trusted-network setting to name this cell's network, so that
    the forwarded-headers stage trusts the load balancer and nothing else.
12. As a security reviewer, I want the task role to read this cell's bucket prefixes and no others,
    so that one cell's task cannot read another cell's releases.
13. As a security reviewer, I want the task role's bucket permissions to name the prefixes the S3
    layout contract defines, so that the grant is checkable against the contract.
14. As a security reviewer, I want the task role to read its client-secret parameter path and no
    wider path, so that a secret belonging to another cell is unreachable.
15. As a security reviewer, I want the task role to hold exactly the four messaging permissions the
    tunnel needs, so that enabling Exec does not become a general grant.
16. As a security reviewer, I want the task role to hold nothing else at all, so that the role is
    reviewable by reading it once.
17. As a security reviewer, I want the execution role separated from the task role, so that pulling
    an image and running the workload are distinct grants.
18. As a security reviewer, I want every permission asserted at plan time, so that a widened grant
    fails a test rather than waiting for a reviewer to notice.
19. As an operator, I want the mock API container defined but joining only once its image exists, so
    that the service can be applied before the mock API is built.
20. As an operator, I want the mock API's client allowlists supplied as inputs rather than hardcoded,
    so that the apps root remains the only place client ids are decided.
21. As an operator, I want the image for each container passed in as a variable, so that a later
    apply never moves an image that a deployment already placed.
22. As an operator, I want the cell's new outputs to match the infrastructure outputs contract, so
    that the seeding and deployment scripts can read them.
23. As a maintainer, I want the plan to contain no resource type outside the cell's intended set, so
    that the later plan-policy row has something consistent to police.
24. As a maintainer, I want every new resource to carry the platform tag, so that the teardown query
    finds it.
25. As a maintainer, I want the module to pass `terraform validate` and `terraform fmt`, so that CI's
    existing infrastructure checks stay green.
26. As a maintainer, I want the module's tests discovered by the test step CI already runs, so that
    no CI change is needed to get the assertions running.
27. As a maintainer, I want the plan assertions to run against a mock provider, so that they need no
    credentials and no network.
28. As the integrator, I want this sub-row to touch only the infrastructure paths its owner holds, so
    that no contract or shared file moves.
29. As the integrator, I want any contract ambiguity reported as a change request rather than decided
    in Terraform, so that the contract stays the single description.
30. As an operator preparing the AWS sitting, I want this Terraform reviewed before the sitting, so
    that the sitting spends its time on applies rather than on authorship.

## Implementation Decisions

- **The resources go into the existing cell module, not a new module.** The module already holds the
  network, the load balancer and the storage, and its own header comment states that the ECS cluster,
  task definition and service are added to it. The platform root keeps passing one set of variables to
  one module instance.
- **The seam is the plan, against a mock provider.** Every claim this sub-row makes is a plan-time
  assertion in the module's own test file. This is the same seam the identity module and the three
  environment roots already use, and the test step CI runs discovers it with no CI change.
- **Two roles, not one.** An execution role for image pull and log writes, and a task role for what
  the workload itself may reach. The task role is the security object of record: this cell's bucket
  prefixes as the S3 layout contract defines them, this cell's client-secret parameter path, and the
  four messaging permissions the tunnel needs. Nothing else. Each of those three grants gets its own
  plan assertion, and one further assertion fails if the role holds a statement the test does not
  recognise, so a widened grant cannot pass by being unmentioned.
- **The task environment is generated from the platform configuration contract**, rendered in the
  environment form the contract describes: a colon becomes a double underscore and every value is a
  string. The bucket, the registry key, the poll interval and the cell name come from the module's own
  resources and variables rather than being restated.
- **The trusted-network setting names this cell's own network**, so the forwarded-headers stage trusts
  the load balancer's addresses and nothing wider.
- **Images are variables with no default that names a tag.** A deployment passes a digest; every other
  apply passes the digest already in state, which is what keeps a routine apply from moving a running
  image. The gateway container is required; the mock API container is defined behind a variable that
  defaults to absent, so the service applies cleanly before that image exists.
- **The mock API's client allowlists are inputs.** The apps root decides client ids, and nothing about
  them is written into the platform root — which is also what keeps onboarding a third app out of this
  root's plan.
- **The health-check path stays the existing variable.** The load balancer row already parameterised
  it for the sample image and the gateway; this sub-row does not reinterpret it.
- **New outputs follow the infrastructure outputs contract.** If a value the scripts need has no place
  in that contract, the gap is a change request, not an invented output key.
- **The platform tag is inherited from the root's default tags**, and a plan assertion proves each new
  resource carries it, because the teardown query and the plan-policy row both depend on it.
- **What this sub-row does not claim.** It does not assert a steady service, a healthy target, a
  successful image pull or a working tunnel. Those are row 1.4's done-when and need the apply. The
  ticket says so, so that nobody reads a green plan as a running cell.
- **This sub-row cannot close row 1.4.** The roadmap generator treats a split id as never landing its
  parent row, and row 1.4's done-when needs AWS. The merge subject names 1.4a only.

## Testing Decisions

- **A good test here is a plan-time assertion on the configuration the module produces**, not a
  restatement of the resource block. It asserts the things a reviewer would otherwise have to hold in
  their head: the architecture and size, the circuit breaker, Exec being on, the log group's
  retention, the environment keys the contract requires, and every statement in the task role.
- **Module under test:** the cell module, planned with a mock AWS provider and no credentials.
- **Prior art to follow:** the identity module's test file, which is the closest match — a mock
  provider, variables declared inline, and one `run` block per claim with `command = plan` and an
  error message naming the plan row the assertion comes from. The three environment roots' test files
  show the same pattern at root level.
- **Permission assertions are written to fail on widening**, not only on absence: the test names the
  expected statements and fails if the role holds one it does not recognise. A role that grows a
  statement must break a test.
- **Each assertion is watched failing first**, by writing it before the resource it describes or by
  mutating the resource once it exists — a wrong architecture, a dropped circuit breaker, a bucket
  prefix widened by one segment — and the observed failures are recorded in the ticket's comments.
- **Formatting and validation are part of the proof**, since the gate already runs `terraform fmt
  -check` and `terraform validate` over every root and module that a change touches.
- **No apply is attempted, and no test requires credentials.** A test that cannot be expressed as a
  plan assertion is evidence that the claim belongs to row 1.4 and the AWS sitting.

## Out of Scope

- Any `terraform apply`, any credential, any live AWS resource, and the state bucket the applies need.
- The row's done-when: a steady service and a healthy target.
- The tunnel and its verification scripts, the seeding and deployment scripts, and the teardown
  script — later rows, one of which also waits on the teardown-role decision.
- The plan-policy row's allowlist. This sub-row gives that row a consistent plan to police; it does
  not write the policy.
- The apps root, the identity root and anything per-app. No client, branding style or secret parameter
  is defined here.
- Deciding the teardown role, the legal cell name, or any other open change request. Where this
  Terraform would need one of those answers, the input is a variable and the question goes in the
  ticket.
- Any edit outside the infrastructure paths workstream 1 owns.

## Further Notes

- The cell module's header comment naming task 1.4 as belonging in it is the strongest evidence that
  the seam is right: the module was written expecting these resources.
- The task role is the reason to do this now rather than during the sitting. It is the one object in
  the cell where a careless grant is both easy to write and hard to notice, and reviewing it under
  plan assertions beforehand is cheaper than reviewing it while an apply waits.
- The open change request about the teardown role touches the bucket policy, not this role. If the
  two are found to overlap while writing the permissions, that overlap is itself worth recording in
  the ticket, because two tickets currently describe the bucket's write and delete rules from
  different directions.
