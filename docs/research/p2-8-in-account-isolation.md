# In-account isolation: what keeps one cell's task role from another cell in one AWS project

- **Ticket**: #1240, "In-account isolation: what IAM, KMS, bucket and network policy can keep one cell's task role from another cell under the managed IAM experience", on the map #1237 (Wayfinder: P2-8 security cells).
- **Date**: 2026-11-16.
- **Branch**: `research/p2-8-in-account-isolation`, pushed and never merged.
- **Method**: Read the repository's Terraform at `origin/main` (`adf0273`) and AWS's public documentation, including AWS's machine-readable Service Reference (the JSON behind the Service Authorization Reference). Nothing was applied, no AWS credential was used, no call was made against the account, the policy simulator was not run and the deployed cell was not read. The `aws-mcp` connector failed to connect, so every AWS fact here is from a public page. Every statement about what a request "would" get is a reading of documented evaluation rules, not an observation. The closing section lists what was executed and what was only read.

Terms: **cell A** is the attacker's cell, **cell B** the target. "The task role" is `spa-platform-<cell>-task` (`infra/modules/cell/iam.tf`). "Project" is the one AWS account in its one selected Region.

## Answer in brief

1. **Today's task role is already cell-scoped by identity policy alone, and nothing on cell B's side grants it anything.** Every statement but one names an ARN that carries the cell name; the one on `*` is `ssmmessages`, which has no resource to name and which P2-9 removes. Cell B's bucket policy holds only `Deny` statements, and no table, parameter, repository or log group has a resource policy. Read against the documented rules, cell A's role gets an implicit deny on every one of cell B's data paths. The drill should confirm that; this finding does not.
2. **That isolation is one layer deep, and it is the layer most easily widened.** In one account, an `Allow` in *either* an identity policy *or* a resource policy is enough (S3, DynamoDB, ECR; KMS is the exception). Nothing today refuses cell A on cell B's side: no cross-cell `Deny`, no permissions boundary, no per-cell key. A second layer is available for each path and section 1 says which.
3. **There is no KMS separation between cells today.** The module creates no KMS key. Client secrets and the key ring sit under the account's one `aws/ssm` key, whose policy cannot be changed and which serves every principal in the account that Parameter Store authorises; the session table uses DynamoDB's AWS owned key; the bucket uses SSE-S3. "Cell B's KMS key" does not exist for the drill to be refused by until a per-cell customer managed key is added.
4. **A per-cell key only separates cells if its key policy does not hand usage to the account root.** The default key policy's root statement delegates `kms:*` to IAM, after which any identity policy in the account can grant the key. The key policy has to name the cell's role for the usage actions, or carry an explicit `Deny` for other principals.
5. **A permissions boundary helps but does not close the resource-policy path.** A boundary's implicit deny does not limit a resource policy that grants to the role *session* ARN, or to `Principal: "*"` with an `aws:PrincipalArn` condition. Only an explicit `Deny` (in the boundary, the identity policy or the resource policy) holds against a permissive resource policy.
6. **Names leak through actions that take no resource.** `s3:ListAllMyBuckets`, `ssm:DescribeParameters`, `dynamodb:ListTables`, `kms:ListKeys`, `kms:ListAliases`, `logs:DescribeLogGroups`, `cognito-idp:ListUserPools`, `ecs:ListClusters` and every `ec2:Describe*` can only be granted on `*`, and then show both cells. The task role holds none of them. `sts:GetCallerIdentity` needs no permission and cannot be denied; it reveals the account id and the caller's own role, nothing of cell B [STS-GCI].
7. **The managed SCPs and RCP permit every mechanism above and supply none of them.** Permissions boundaries on the project's own roles, bucket, table, repository and key policies, and `aws:PrincipalTag` conditions are all allowed. The project cannot author an SCP or RCP of its own, so no account-level guardrail between cells is available; the separation has to live in IAM, resource and key policies. The RCP only keeps principals from outside the organisation out, and does not cover Systems Manager.
8. **Two VPCs in one account share no route by default.** Peering, a private hosted zone association, a shared endpoint or VPC Lattice would join them; all are permitted by the SCPs and must be held by a plan check. Transit gateways and VPN are denied by the tier SCP. Network separation does not protect cell B's *data*: cell A's task reaches S3, Parameter Store and DynamoDB at their public or gateway endpoints regardless, so IAM is the only control on those paths.
9. **AWS's own position is that the account is the isolation boundary.** In one account the cells share IAM itself, service quotas and API rate limits, account-wide settings and every resource-less list call. This is the gap the map already records for the adopter; section 4 lists what it concretely contains.

## 1. Each data path: what scopes it, and what cannot be scoped

### The evaluation rules that decide everything below

- **Same account: either policy can grant.** "When an IAM entity (user or role) requests access to a resource within the same account, AWS evaluates all the permissions granted by the identity-based and resource-based policies. The resulting permissions are the union of the permissions of the two types. If an action is allowed by an identity-based policy, a resource-based policy, or both, then AWS allows the action. An explicit deny in either of these policies overrides the allow." [IAM-EVAL]
- **KMS and role trust policies are the exceptions.** "For most resources, you only need an explicit `Allow` for the principal in either an identity-based policy or a resource-based policy to grant access. IAM role trust policies and KMS key policies are exceptions to this logic, because they must explicitly allow access for principals." [IAM-FLOW]
- **Who a resource policy names changes what a boundary can stop.** A grant to the role ARN is "limited by an implicit deny in a permissions boundary or session policy". A grant to the role *session* ARN is "not limited by an implicit deny in an identity-based policy, a permissions boundary, or session policy". And: "Permissions boundaries and session policies do not limit permissions granted using the `aws:PrincipalArn` condition key with a wildcard(\*) in the Principal element, unless the identity-based policies contain an explicit deny." [IAM-FLOW]
- **AWS's own example of the boundary's limit.** "if a resource-based policy attached to a Secrets Manager secret allows Nikhil to perform the `secretsmanager:GetSecretValue` action, then Nikhil can retrieve and decrypt the secret. The reason is that Secrets Manager operations are not explicitly denied by his permissions boundary, and implicit denies in permissions boundaries do not limit resource-based policies." [IAM-PB] (That example's principal is an IAM user; for a role the three cases above apply.)
- **An explicit deny anywhere wins.** "If the enforcement code finds even one explicit deny that applies, the enforcement code returns a final decision of **Deny**." [IAM-FLOW] A boundary may itself hold `Deny` statements, as AWS's example boundary does [IAM-PB].

So in one account three things can give cell A's role a read of cell B: a widened identity policy on cell A's role, an `Allow` in a resource policy on cell B's resource, or a key policy that delegates to the account. An explicit `Deny` is the only statement that survives all three.

### Per path

The "resource" and "tag key" columns are from AWS's Service Reference JSON [SAR], read on 2026-11-16. "ABAC" is the column of that name in AWS's service table [IAM-SVC].

| Path | What the task role holds today | Resource-level? | Resource policy exists as a type? | Identity policy alone enough? | Second layer available | Actions that take no resource (reveal names) |
| --- | --- | --- | --- | --- | --- | --- |
| **S3** | `s3:GetObject` on `<bucket>/registry/*` and `/apps/*` | Yes: `GetObject` on `object`, `ListBucket` on `bucket` | Yes, bucket policy. Same-account grant by bucket policy alone works [S3-AUTH] | Yes today: cell B's bucket policy has no `Allow` | An explicit `Deny` in cell B's bucket policy for principals of another cell. Bucket tags as a condition need ABAC switched on per bucket: "By default, ABAC is disabled for all Amazon S3 general purpose buckets" [S3-ABAC] | `ListAllMyBuckets` (8 list actions in all) |
| **Parameter Store** | `GetParameter`, `GetParameters` on `/spa-platform/<cell>/apps/*`; `PutParameter`, `GetParametersByPath` on the key-ring path; `AddTagsToResource` with tag conditions | Yes, all on `parameter` | Only for advanced-tier parameters, through AWS RAM sharing [SSM-SHARE]; the service table says "Partial". The cell's parameters are standard tier, so there is nowhere on the resource side to put a `Deny` | Yes today | A permissions boundary or an identity-side `Deny`; a per-cell customer managed key (below). `aws:ResourceTag` is available on `GetParameter`, `GetParameters`, `GetParameterHistory`, `DeleteParameter` but **not** on `GetParametersByPath` (its only key is `ssm:Recursive`) or `PutParameter` | `DescribeParameters`: every parameter name in the account, so cell B's app ids and ring entries |
| **DynamoDB** | `GetItem`, `PutItem`, `DeleteItem`, `Query`, `BatchWriteItem` on the table and its `appId` index | Yes, `table` and `index` | Yes. "If the IAM principal is from the same account as the resource owner, a resource-based policy is sufficient to specify access permissions to the resource" [DDB-RBP] | Yes today: no table has a resource policy | An explicit `Deny` in a table resource policy; `aws:ResourceTag` is listed on the `table` and `index` resource types | `ListTables`, `ListBackups`, `ListGlobalTables`, `DescribeLimits`, `DescribeEndpoints` |
| **KMS** | Nothing. No `kms:` action, and no key in the module | Yes for use (`Decrypt`, `Encrypt`, `GenerateDataKey`, `DescribeKey` on `key`) | Yes, and it is mandatory: the key policy is the gate | Not applicable today (see below) | The key policy itself | `ListKeys`, `ListAliases`, `CreateKey`, `GenerateRandom`, `ListRetirableGrants` |
| **ECR** | Nothing on the task role. The execution role pulls from this cell's two repositories and holds `GetAuthorizationToken` on `*` | Yes, `repository`, including `DescribeRepositories` and `ListImages` | Yes. "A user or role only needs to be allowed permission for an action through either a repository policy or an IAM policy but not both" [ECR-RP] | Yes today: no repository policy | An explicit `Deny` in a repository policy | `GetAuthorizationToken` (a token for the whole registry; the pull is still authorised per repository), `DescribeRegistry`, `GetRegistryPolicy` |
| **CloudWatch Logs** | Nothing on the task role. The execution role writes to this cell's group | Yes for reads (`GetLogEvents` on `log-stream`; `FilterLogEvents`, `StartQuery`, `DescribeLogStreams` on `log-group`) | Account-level resource policies exist; ABAC is "Partial" | Yes today | Identity-side `Deny` or boundary | `DescribeLogGroups`, `ListLogGroups`, `DescribeQueries`, `DescribeResourcePolicies` (26 list actions in all) |
| **Cognito** | Nothing. The gateway reaches the pool over its OAuth endpoints, not the IAM-authorised API | Yes for the IAM-authorised admin calls (`userpool`) | No [IAM-SVC] | Yes for the admin API | Not an IAM matter (below) | `ListUserPools`, `DescribeUserPoolDomain`; and the public operations `InitiateAuth`, `SignUp`, `GetUser`, `RevokeToken`, which carry no IAM authorisation at all |

Two rows need more than the table holds.

**KMS.** A key policy is not like the other resource policies: "No AWS principal, including the account root user or key creator, has any permissions to a KMS key unless they are explicitly allowed, and never denied, in a key policy, IAM policy, or grant", and "Without permission from the key policy, IAM policies that allow permissions have no effect." [KMS-KP] That makes a per-cell key a real second gate, with one trap. The default key policy's single statement names the account root with `kms:*`, and "When the principal in a key policy statement is the account principal, the policy statement doesn't give any IAM principal permission to use the KMS key. Instead, it allows the account to use IAM policies to *delegate* the permissions specified in the policy statement." [KMS-DEFAULT] A cell key created with the default policy is therefore gated by identity policies only, the same single layer as everything else. For the key to separate cells, its policy must either grant the usage actions to the cell's role by name and keep the root statement to administration, or keep the root statement and add an explicit `Deny` on the usage actions for every principal that is not the cell's. KMS "supports all global condition keys. You can use them in AWS KMS key policies and IAM policies" [KMS-GLOBAL], so `aws:PrincipalArn` or `aws:PrincipalTag/Cell` can carry that `Deny`. `aws:ResourceTag` on a key is "IAM policy only", and tag changes "might take up to five minutes" to affect authorisation; with tag-based key access, "permission to manage tags and aliases is now an access control permission" [KMS-ABAC]. Parameter Store calls KMS with the encryption context `PARAMETER_ARN`, so a `kms:Decrypt` grant can also be held to the cell's parameter path with `kms:EncryptionContext:PARAMETER_ARN` [KMS-PS]. Grants (`kms:CreateGrant`) are a third way to pass key access and belong to no task role.

**Cognito.** IAM does not stand between cell A and cell B's pool. Cognito has "three authorization models: IAM-authorized, public, and token-authorized", and "With public (unauthenticated) authentication requests, no authorization is required" [COG-AUTH]. The hosted OAuth endpoints are likewise reached with a client id and secret, not a signature. So the drill's attempt against cell B's identity provider is refused, if it is, by the client secret and the pool, and the expected refusal is an OAuth or Cognito error, not an IAM `AccessDenied`. Cell A's secret is useless against cell B's client only if the two clients have different secrets, which they do when each is generated (`generate_secret = true` in `infra/envs/poc/apps/main.tf`), and cell B's secret is in a parameter cell A's role cannot read.

### Tags as the cell's attribute

Every resource the platform root creates already carries `Cell = <cell>` through the provider's default tags (`infra/envs/poc/platform/main.tf`), and that includes the task role, so `aws:PrincipalTag/Cell` is present on the task's requests: the key "is included for a principal using an IAM role with attached tags or session tags" [IAM-KEYS]. A single statement comparing `aws:ResourceTag/Cell` with `${aws:PrincipalTag/Cell}` is expressible. It is not uniform:

- S3 general purpose buckets ignore bucket tags in authorisation until ABAC is enabled per bucket [S3-ABAC].
- `ssm:GetParametersByPath`, the call that reads the key ring, takes no tag key [SAR].
- KMS accepts `aws:ResourceTag` in IAM policies only [KMS-ABAC].
- Whoever can tag can then grant. AWS says so of KMS [KMS-ABAC]; it follows for every other service. The task role can tag only its own ring parameters, and only with its own cell's value (`TagKeyRingOnCreate`).

Read together: tags can carry a cross-cell `Deny` on the principal side (`aws:PrincipalTag/Cell` in a bucket, table, repository or key policy), where they need no per-service support on the resource. ARN patterns remain the dependable way to say *which resources*.

### What a second cell's role could already reach in today's Terraform

Read from `infra/` at `adf0273`. None of these is a cross-cell data read by a task role; each is something two cells would share or that the drill should know.

1. **The `aws/ssm` key.** Both cells' client secrets and key rings are SecureStrings under the one AWS managed key (`infra/envs/poc/apps/main.tf`, `iam.tf`'s `ReadClientSecrets` comment). An AWS managed key "can only be used in the context of the AWS service you're operating in and it can only be used by principals within the account" and "you cannot establish access control policies for the default `aws/ssm` KMS key" [KMS-CONCEPTS] [KMS-PS]. The only thing between cell A and cell B's secret is `ssm:GetParameter` scoping.
2. **No key on the session table or the bucket.** DynamoDB's "AWS owned key – Default encryption type" [DDB-ENC] and SSE-S3 give no key policy to refuse anyone.
3. **The trust policy is account-wide.** `ecs_tasks_trust` admits ECS on behalf of `arn:...:ecs:<region>:<account>:*`. AWS documents that this cannot be narrower: "Using the `aws:SourceArn` condition key to specify a specific cluster is not currently supported, you should use the wildcard to specify all clusters" [ECS-ROLE]. So a task in cell A's cluster can run under cell B's task role if someone with `iam:PassRole` on that role registers it. That is a deployer-side risk, the one the map lists under "the people and pipelines above the cells", not something cell A's task role can do: it holds no `iam:` or `ecs:` action.
4. **`ssmmessages:*Channel` on `*`.** These four have no resource type but one (`OpenDataChannel` takes `session`) and are the agent side of ECS Exec. Opening a session into cell B's task needs `ecs:ExecuteCommand`, which no task role holds. P2-9 removes the statement.
5. **Cell names that prefix one another.** The task role's ARNs are exact or end at a `/` path segment, so cell `red` does not match `/spa-platform/red-2/...`. The CI plan role's `role/spa-platform-<cell>-*` and `policy/spa-platform-<cell>-*` (`infra/bootstrap/ci-role.tf`) do match: the plan role of cell `red` reads the roles of cell `red-2`. Two cell names must not be a hyphen-prefix of one another, or that pattern needs a delimiter.
6. **The bootstrap, identity-provider and apps roots are per cell by variable.** `infra/bootstrap`, `infra/envs/poc/idp` and `infra/envs/poc/apps` each take `cell`, so a second cell today means a second state bucket, CI role, teardown role and pool. The CI plan role describes EC2, ECS, ECR, logs and Cognito on `*`, so each cell's plan role sees the other cell's configuration. That is the level above the cells and is by design read-only, but it is a principal that reaches both.
7. **Test-user passwords** are at `/spa-platform/<cell>/test-users/*` (`infra/modules/idp-cognito/main.tf`), outside the task role's `apps/*` path, so neither cell's task role reads its own or the other's.
8. **Both cells default to `10.20.0.0/16`** (`infra/envs/poc/platform/variables.tf`). See section 3.

## 2. What the managed SCPs and RCP permit

Read from AWS's page for the new experience [NEW-SCP] on 2026-11-16. The page gives the policies as JSON; the statements below are from that JSON.

- **The project cannot write its own.** "AWS manages the organization management policies including the resource control policies (RCPs) and the service control policies (SCPs)", and each policy is described as one that "cannot be changed" or "cannot be modified". There is therefore no account-level statement the project can add to hold cells apart. An SCP is in any case a guardrail and not a grant [ORG-SCP]. Activating advanced features changes which policies apply; that is outside this ticket.
- **Permissions boundaries: permitted.** The only IAM role statement, `ProtectManagedRoles`, denies `iam:PutRolePermissionsBoundary`, `iam:DeleteRolePermissionsBoundary`, `iam:CreateRole`, `iam:PutRolePolicy` and the like on `arn:*:iam::*:role/managed/*` alone. The platform's roles are not under `/managed/`. `DenyIAMRestrictedActions` in the tier policies covers aliases, identity providers, login profiles, groups and certificates, none of which a boundary needs. `iam:*` is in both tiers' allow lists.
- **Resource policies: permitted.** `s3:*`, `dynamodb:*`, `kms:*`, `ecr:*`, `logs:*`, `ssm:*` and `ram:*` are in both tiers' allow lists, and no statement denies `PutBucketPolicy`, `PutResourcePolicy`, `PutKeyPolicy` or `SetRepositoryPolicy`.
- **`aws:PrincipalTag` conditions: permitted.** No SCP statement restricts condition keys, and `iam:TagRole` is denied only on `/managed/` roles.
- **The RCP does not separate cells.** `DenyAnyoneOutsideMyOrgAndAWS` denies `s3:*`, `kms:*`, `dynamodb:*`, `ecr:*`, `logs:*`, `cognito-idp:*`, `sts:*` and others to principals whose organisation is not the resource's. Both cells' roles are in the one account, so it never fires between them. Systems Manager is not in its action list, so Parameter Store has no RCP cover; with standard-tier parameters there is no resource policy that could admit an outsider, so nothing is exposed by that.
- **Region.** The Region SCPs hold everything to the selected Region plus named global actions in `us-east-1` and `us-west-2`. A per-cell KMS key is a single-Region key in the selected Region, which is allowed.
- **Network actions.** `ec2:*`, `route53:*`, `route53resolver:*`, `ram:*` and `servicediscovery:*` are allowed, so VPC peering, private hosted zone association and endpoint creation are all possible. `DenyTransitGatewayAndVPN` denies transit gateways, their VPC attachments, Client VPN and site-to-site VPN in both the Free Tier and Paid Plan policies, and `DenyCloudWAN` denies Cloud WAN. So of the things that join two VPCs, only the transit gateway is closed by AWS; the rest must be closed by the platform.
- **The spend limit bears on a second cell.** As a project "nears the spend limit", an SCP denies, among others, `ecs:CreateService`, `elasticloadbalancing:CreateLoadBalancer`, `ec2:CreateNatGateway`, `dynamodb:CreateTable`, `ecr:CreateRepository`, `kms:CreateKey` and `cognito-idp:CreateUserPool`. A second cell stood up near the limit fails on creation, and a per-cell key cannot be created then either. The page does not say at what fraction of the limit this applies.

## 3. Network

**What separates two VPCs by default.** A VPC "is logically isolated from other virtual networks in the AWS Cloud" [VPC-HOW]. Two VPCs in one account have no route to each other until something is added, and a security group can name a group in another VPC only when "There is a peering connection between the VPCs" or "a transit gateway between the VPCs" [SG-RULES]. Each cell's two groups admit only each other (`network.tf`), and the default group is emptied.

**What would join them.**

| Joiner | What it takes | Held by |
| --- | --- | --- |
| VPC peering | A request, an acceptance (the same account can do both), and a route in each VPC's tables; then security groups may reference each other [PEER] [PEER-SG] | Nothing in AWS. A plan check must refuse `aws_vpc_peering_connection` and any route to another cell's range |
| Transit gateway | Attachments and routes | Denied by the tier SCP |
| Private hosted zone associated with both VPCs | "After you create the hosted zone you can associate more VPCs with it"; in one account the console does it directly [R53-PHZ] [R53-ASSOC] | Nothing in AWS. It joins names, not routes: cell A would resolve cell B's internal names. Assumption A1 gives each cell its own zone, and a plan check must hold each zone to one VPC |
| Shared interface endpoint, PrivateLink service or VPC Lattice | An endpoint in one cell's VPC to a service fronting the other's ALB | Nothing in AWS. The module's resource-type allowlist already refuses types it does not name |
| A public address on a task | In the single-task shape the task has one (`assign_public_ip = !var.multi_task`) | The task security group admits the gateway port from its own ALB's group only (P-06), so the address accepts nothing from cell A |

**What the network does not do.**

- **It does not protect data.** Cell A's task sends 443 to `0.0.0.0/0`, and S3, Parameter Store, DynamoDB, ECR, CloudWatch Logs and Cognito are all reached at AWS endpoints outside both VPCs. Cell B's bucket is exactly as reachable from cell A's VPC as from cell B's. IAM is the whole control.
- **A VPC condition could add a layer, but only for two services and only in the two-task shape.** `aws:SourceVpc` and `aws:SourceVpce` are "included in the request context only if the requester uses a VPC endpoint to make the request" [IAM-KEYS]. Under `multi_task` the cell has gateway endpoints for S3 and DynamoDB only; Parameter Store, ECR and logs go through the NAT gateway and carry no VPC key. In the single-task shape nothing does. A bucket or table `Deny` on `aws:SourceVpce` would also refuse `spactl`, CI and the operator, who are outside the VPC, unless carved out [S3-VPCE]. The gateway endpoints carry "the default endpoint policy", which "grants full access" [VPCE-POL]; an endpoint policy naming only the cell's own bucket and table would stop cell A's *network path* from carrying a request for cell B's, again for those two services only.
- **An internal load balancer's name is public.** "The DNS name of an internal load balancer is publicly resolvable to the private IP addresses of the nodes" [ELB-HOW]. Cell A can resolve cell B's ALB name to private addresses if it knows the name; it cannot route to them.
- **Overlapping ranges blur the drill.** Both cells default to `10.20.0.0/16`. From cell A, cell B's ALB address is then an address inside cell A's own VPC, so "connection timed out" or even "connected" would say nothing about cell B. Give the cells distinct ranges for the drill. (An overlap does make peering impossible, since the accepter "cannot have a CIDR block that overlaps" [PEER], but that is not a control worth the ambiguity.)

## 4. What AWS documents as the limit of in-account isolation

AWS's guidance is that the account is the boundary. The Well-Architected security pillar: "Account-level separation is strongly recommended, as it provides a strong isolation boundary for security, billing, and access", and "AWS accounts provide a security isolation boundary between workloads or resources that operate at different sensitivity levels." [WA-SEC01] The multi-account whitepaper: "By using separate accounts for the non-production and production environments, by default, the resources and data that make up a workload environment are separated from other environments and workloads", and account-level isolation "helps ensure that if an application-level problem, misconfiguration, or malicious activity occurs within one account, the impacts can be contained". [WP-MULTI]

The operative words are "by default". Across accounts, access needs an allow on both sides; inside one account, separation is something written and kept written. What two cells in one account concretely share:

- **IAM itself.** One set of roles and policies; any principal with `iam:` write, `iam:PassRole` or a resource-policy write reaches both cells. Within the account an allow on either side is enough [IAM-EVAL].
- **Quotas and API rate limits.** "Because Service Quotas and request rate limits are allocated for each account, use of separate accounts for workloads can help distribute the potential impact of the quotas and limits." [WP-MULTI] One cell can exhaust what the other needs. This is the "noisy neighbour" item under the map's "Beyond reads".
- **Resource-less actions.** The list and describe calls in section 1 cannot be held to a cell. The only control is not granting them.
- **Account-wide settings.** A setting that exists once per account and Region cannot belong to a cell. The module already meets one: ECR's registry scanning configuration, which `storage.tf` avoids for that reason. The `aws/ssm` key is another.
- **The task-role trust.** ECS cannot be told which cluster may assume a role [ECS-ROLE].
- **No guardrail above.** In an organisation the adopter runs, an SCP or RCP could hold cells apart from outside the account. In this project those policies are AWS's [NEW-SCP].

What in-account isolation can honestly claim, then: with the layers in section 1 in place, a compromised gateway process holding cell A's task role credentials is refused on every read of cell B's data, by at least two independent statements per path where the service allows two. It cannot claim separation against a principal that can change IAM, against exhaustion of shared quotas, or of names from anyone granted a list call.

## What was executed and what was only read

**Executed** (2026-11-16, on this machine, no AWS credentials):

- `git fetch origin` and a worktree at `origin/main` `adf0273`; `grep` and file reads over `infra/`.
- `curl` of AWS's public Service Reference JSON for eleven services, and a local script over those files that printed, per action, its resource types and condition keys. The per-action facts in section 1's table are that script's output, not a reading of the rendered HTML pages (which need JavaScript and returned only a title).
- HTTP fetches of the documentation pages below, with quoted passages copied from the fetched text.

**Not executed**: `terraform plan`, `terraform test` or `apply`; any AWS CLI or SDK call; the IAM policy simulator; Access Analyzer; any read of the deployed cell; any drill. No claim here that a request "is refused" has been observed.

**Not verified, stated from the rules only**:

- That cell A's role is refused on each of cell B's paths (answer 1). It follows from the Terraform and the evaluation rules; the plan-policy check and the drill are what establish it.
- That `aws:PrincipalTag/Cell` reaches the request context for an ECS task role session. The IAM page says role tags do; no ECS-specific statement was found.
- That the execution role's credentials are unreachable from inside the container on Fargate. The ECS pages read describe the execution role as the agent's and the credentials endpoint as vending the task role; no sentence stating the negative was found.
- The threshold at which the spend-limit SCP applies.
- Whether DynamoDB tag-based authorisation needs any account-level enablement; the page read did not say.

**Read** (all on 2026-11-16):

Repository, at `adf0273`: `infra/modules/cell/iam.tf`, `network.tf`, `storage.tf`, `sessions.tf`, `ecs.tf` (by grep), `variables.tf` (by grep); `infra/envs/poc/platform/main.tf` and `variables.tf` (by grep); `infra/envs/poc/apps/main.tf` (by grep); `infra/modules/idp-cognito/main.tf` (by grep); `infra/bootstrap/ci-role.tf`, `teardown-role.tf`, `main.tf` (by grep); `docs/agents/aws-rules.md`; `docs/agents/issue-tracker.md`.

AWS:

- [NEW-SCP] Managed policies for your organization: https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html
- [SAR] Service Reference JSON, version v1.5, for `s3`, `ssm`, `dynamodb`, `kms`, `ecr`, `logs`, `cognito-idp`, `ssmmessages`, `sts`, `ecs`, `ec2`: `https://servicereference.us-east-1.amazonaws.com/v1/<service>/<service>.json`
- [IAM-EVAL] Policy evaluation logic: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_evaluation-logic.html
- [IAM-FLOW] How AWS enforcement code logic evaluates requests: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_evaluation-logic_policy-eval-denyallow.html
- [IAM-PB] Permissions boundaries for IAM entities: https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies_boundaries.html
- [IAM-KEYS] AWS global condition context keys: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html
- [IAM-SVC] AWS services that work with IAM: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_aws-services-that-work-with-iam.html
- [IAM-TAGS] Controlling access to AWS resources using tags: https://docs.aws.amazon.com/IAM/latest/UserGuide/access_tags.html
- [ORG-SCP] Service control policies: https://docs.aws.amazon.com/organizations/latest/userguide/orgs_manage_policies_scps.html
- [KMS-KP] Key policies in AWS KMS: https://docs.aws.amazon.com/kms/latest/developerguide/key-policies.html
- [KMS-DEFAULT] Default key policy: https://docs.aws.amazon.com/kms/latest/developerguide/key-policy-default.html
- [KMS-ABAC] ABAC for AWS KMS: https://docs.aws.amazon.com/kms/latest/developerguide/abac.html
- [KMS-GLOBAL] AWS global condition keys in KMS: https://docs.aws.amazon.com/kms/latest/developerguide/conditions-aws.html
- [KMS-CONCEPTS] AWS KMS concepts: https://docs.aws.amazon.com/kms/latest/developerguide/concepts.html
- [KMS-PS] KMS encryption for Parameter Store SecureString parameters: https://docs.aws.amazon.com/kms/latest/developerguide/services-parameter-store.html
- [SSM-SHARE] Working with shared parameters in Parameter Store: https://docs.aws.amazon.com/systems-manager/latest/userguide/parameter-store-shared-parameters.html
- [S3-AUTH] How Amazon S3 authorizes a request for an object operation: https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-control-auth-workflow-object-operation.html
- [S3-ABAC] Enabling ABAC in general purpose buckets: https://docs.aws.amazon.com/AmazonS3/latest/userguide/buckets-tagging-enable-abac.html
- [S3-VPCE] Controlling access from VPC endpoints with bucket policies: https://docs.aws.amazon.com/AmazonS3/latest/userguide/example-bucket-policies-vpc-endpoint.html
- [DDB-RBP] Authorization with IAM identity-based policies and DynamoDB resource-based policies: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/rbac-auth-iam-id-based-policies-DDB.html
- [DDB-ENC] DynamoDB encryption at rest: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/EncryptionAtRest.html
- [ECR-RP] Private repository policies in Amazon ECR: https://docs.aws.amazon.com/AmazonECR/latest/userguide/repository-policies.html
- [ECS-ROLE] Amazon ECS task IAM role: https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html
- [COG-AUTH] Authorization models for API and SDK authentication (Cognito user pools): https://docs.aws.amazon.com/cognito/latest/developerguide/authentication-flows-public-server-side.html
- [STS-GCI] GetCallerIdentity ("No permissions are required to perform this operation"): https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html
- [VPC-HOW] How Amazon VPC works: https://docs.aws.amazon.com/vpc/latest/userguide/how-it-works.html
- [SG-RULES] Security group rules: https://docs.aws.amazon.com/vpc/latest/userguide/security-group-rules.html
- [PEER] VPC peering basics: https://docs.aws.amazon.com/vpc/latest/peering/vpc-peering-basics.html
- [PEER-SG] Update your security groups to reference peer security groups: https://docs.aws.amazon.com/vpc/latest/peering/vpc-peering-security-groups.html
- [R53-PHZ] Working with private hosted zones: https://docs.aws.amazon.com/Route53/latest/DeveloperGuide/hosted-zones-private.html
- [R53-ASSOC] Associating more VPCs with a private hosted zone: https://docs.aws.amazon.com/Route53/latest/DeveloperGuide/hosted-zone-private-associate-vpcs.html
- [VPCE-POL] Control access to VPC endpoints using endpoint policies: https://docs.aws.amazon.com/vpc/latest/privatelink/vpc-endpoints-access.html
- [ELB-HOW] How Elastic Load Balancing works: https://docs.aws.amazon.com/elasticloadbalancing/latest/userguide/how-elastic-load-balancing-works.html
- [WA-SEC01] SEC01-BP01 Separate workloads using accounts: https://docs.aws.amazon.com/wellarchitected/latest/security-pillar/sec_securely_operate_multi_accounts.html
- [WP-MULTI] Benefits of using multiple AWS accounts: https://docs.aws.amazon.com/whitepapers/latest/organizing-your-aws-environment/benefits-of-using-multiple-aws-accounts.html
