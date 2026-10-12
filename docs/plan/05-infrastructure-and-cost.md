# Infrastructure and cost

Phase 1 is one ALB, one task, one bucket and one user pool. Left running, it costs about $30 a month.

## Terraform boundaries

Three roots, three states, and a one-time bootstrap. Each root changes for one reason.

| Root | Owns | Changes when | Never contains |
| --- | --- | --- | --- |
| `bootstrap` | The Terraform state bucket, and the read-only role CI assumes for plans | Once | Anything else |
| `envs/poc/idp` | User pool, managed-login domain, resource servers, test users | The identity provider setup changes | App clients |
| `envs/poc/apps` | For each app definition: one Cognito client, its branding style and one secret parameter | An app is onboarded or removed | Runtime resources |
| `envs/poc/platform` | VPC, subnets, security groups, ALB, listener, target group, ECS cluster, task definition, service, bucket, image repositories, roles, log group | The platform changes, or a new gateway image is deployed | Any per-app resource |

State lives in an S3 backend with one key per root and the native lock file, which needs Terraform 1.11 or later. The state bucket is a second bucket in the account. It holds no platform data, and the brief's "one private bucket" is the platform's.

Apps 1 and 2 are created through the apps root, exactly like app 3. The onboarding path is therefore exercised from the first day, not only at G5.

The platform root reads two values from the apps root's state: the client ids of apps 1 and 2, for the mock API's allowlists. App 3 is not among them, so onboarding it leaves the platform plan empty.

### What stays out of Terraform

- **Registry content and SPA releases.** They are data, written by `spactl` with conditional writes.
- **Container images.** `deploy.sh` builds and pushes them. Terraform receives the digest as a required variable, and any other apply passes the digest already in state.
- **The local certificate authority.** `certs.sh` creates it and imports the certificate. Terraform finds the certificate in ACM by its domain name.

### First deployment

The order matters once, because each step feeds the next.

1. `certs.sh`, then the bootstrap.
2. The identity root, then the apps root. Cognito now has two clients.
3. The platform root, on the sample image. The health check follows the image: `/` for the sample, `/readyz` for the gateway.
4. `seed.sh` publishes the two fixture releases and writes the first registry.
5. `deploy.sh` builds both images and waits for a steady service. The target turns healthy only once `/readyz` reports a snapshot.
6. `tunnel.sh`, then `verify-tunnel.sh`.

A gateway that starts against an empty bucket never becomes ready, so `deploy.sh` refuses to run before step 4.

### What the deployer needs

Three kinds of run touch the AWS account: every Terraform apply, G0, and the deployed gate runs. Whoever does them needs the list below. CI needs a read-only role to produce plans, which the bootstrap creates. Workstreams 2, 4 and 5 need none of it. The dependency list in the repository, at docs/dependencies.md, has the versions checked on 11 October 2026 and the install commands.

- AWS credentials for the account, with rights to create the resources above
- Terraform 1.11 or later, the AWS CLI and the Session Manager plugin
- Docker with ARM64 builds
- The .NET 10 SDK and access to NuGet
- A browser the test runner can drive, and rights to trust a local certificate authority and edit the hosts file

### Phase 1 topology

- **ALB:** internal, two subnets, HTTPS on 443 only, host header preserved, invalid header fields dropped.
- **Task:** ARM64, 0.25 vCPU, 1 GB, two containers. The gateway listens on 8080. The mock API listens on loopback ports 5080 and 5081.
- **Security groups:** the ALB accepts 443 from the task group only, which is the tunnel's path. The task accepts 8080 from the ALB group only. The task sends 443 out from a public address, for Cognito, S3, Parameter Store, ECR and logs. No NAT gateway.
- **Bucket:** private, versioned, TLS only. Releases under `apps/` are write-once and cannot be deleted.

`modules/cell` takes a cell name, an address range, a certificate, image digests and the issuer settings. A Phase 2 cell is one more instance of it.

## Workstation access

The ALB is internal, so a workstation reaches it through a Session Manager port forward, with the gateway task as the hop. No bastion is created.

1. `certs.sh` creates a local certificate authority and a certificate for `*.spa-poc.test`. It trusts the authority on the workstation and imports the certificate into ACM for the listener.
2. Add `127.0.0.1 app1.spa-poc.test app2.spa-poc.test app3.spa-poc.test` to the hosts file.
3. `tunnel.sh` finds the running task and forwards local port 8443 to the ALB on 443.
4. `verify-tunnel.sh` requests the health path, which is `https://app1.spa-poc.test:8443/healthz` once the gateway runs. It fails unless TLS validates against the local authority. This is the "verified tunnel" in the brief.
5. Browse to `https://app1.spa-poc.test:8443/`. Cognito redirects back to the same host and port, which is why the port appears in the registry `host` and in every callback URL.

```bash
aws ssm start-session \
  --target "ecs:${CLUSTER}_${TASK_ID}_${RUNTIME_ID}" \
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters "{\"host\":[\"${ALB_DNS}\"],\"portNumber\":[\"443\"],\"localPortNumber\":[\"8443\"]}"
```

The tunnel carries TCP, so the browser's TLS handshake reaches the ALB unchanged and the wildcard certificate is the one presented.

Three conditions come with this route. The task must belong to a service with ECS Exec enabled. Its role needs the four `ssmmessages` permissions. Its root filesystem must be writable. All three end in Phase 2, where access comes over the corporate network.

## Deployment and rollback

Four levers, fastest first. The first two need no deployment.

| Lever | Changes | How | Live after | Rollback |
| --- | --- | --- | --- | --- |
| SPA release | Which release an app serves | `spactl release publish`, then `spactl release activate` | One poll interval | Activate the previous release |
| Registry | Any entry: a new app, an OAuth client, APIs, CSP | `spactl registry validate`, then `spactl registry apply` | One poll interval | `spactl registry rollback` republishes the previous object version under a new revision |
| Gateway | The container image | `deploy.sh` builds, pushes, applies the new digest and checks that the running task has it | One rolling replacement | `rollback.sh` applies the digest of the previous task definition. The deployment circuit breaker also rolls back on failed health checks. |
| Infrastructure | Terraform resources | A reviewed plan, then apply | Per resource | Revert the commit and apply |

In Phase 1 a gateway deployment signs every user out, because sessions and encryption keys live in the task's memory. Phase 2 removes that.

### Onboarding a third SPA

1. Copy `fixtures/onboarding/app3.json` into `fixtures/apps/` and apply the apps root. The plan shows one client, one branding style and one parameter.
2. `spactl release publish app3 fixtures/spa/app3 2026-10-10.1`
3. `spactl app add fixtures/apps/app3.json --release 2026-10-10.1` builds the entry from the definition and the new client's outputs, validates it and applies the registry.
4. `spactl wait --revision N` returns when `/readyz` reports the new revision.
5. Browse to the new host. The platform root's plan is still empty and the task has not restarted.

An app definition is a registry entry without the client id, the secret reference and the release. Terraform reads it to create the client, and `spactl` completes it. Each app is described once.

### Rotating a client secret

1. Add a second secret to the client at the provider. Cognito has allowed two active secrets per client since February 2026.
2. Write it as a new version of the app's parameter.
3. Point the entry's `clientSecretRef` at the new version and apply the registry.
4. After `spactl wait`, delete the old secret at the provider.

Both secrets are valid between steps 1 and 4, so no login or refresh fails. Terraform's Cognito client resource may not expose the second secret yet, so steps 1 and 4 use the AWS CLI. Terraform ignores changes to the parameter's value, so the next apply does not put the old secret back.

### Teardown

`teardown.sh` empties the bucket and repositories, destroys the three roots in reverse order, deletes the imported certificate and the inactive task definitions, and then queries for the platform tag. Any resource still tagged fails the script. The tagging API can lag a deletion, so the query retries for a few minutes first. The state bucket goes last, by hand.

## Cost model

### Phase 1, left running

us-east-1, 730 hours a month.

| Item | Unit price | Per month |
| --- | --- | --- |
| Application Load Balancer | $0.0225 per hour, plus $0.008 per capacity-unit hour | $16.43, plus cents at this traffic |
| Fargate task, ARM64, 0.25 vCPU and 1 GB | $0.03238 per vCPU-hour and $0.00356 per GB-hour on ARM, about 20% below x86 | $8.51 |
| Public IPv4 address on the task | $0.005 per hour | $3.65 |
| CloudWatch Logs | About $0.50 per GB ingested | Under $1 |
| S3 and ECR storage, including the state bucket | Cents per GB | Under $0.50 |
| Cognito, Essentials plan | First 10,000 monthly active users free | $0 |
| Parameter Store standard parameters, imported certificate, Session Manager | No charge | $0 |
| **Total** |  | **About $30** |

The ALB is the floor at $16.43. Scaling the service to zero when idle saves about $12. Destroying everything between sessions brings the cost to cents.

The ALB, public address and Cognito prices were read from AWS pages. The Fargate rates come from a third-party summary of the list prices, and the CloudWatch Logs rate is from memory. The pages are in Sources on the Plan tab.

### Cost of one more SPA

Storage for its releases, one standard parameter, one Cognito client and its branding style. The last three are free. There is no compute, listener rule, certificate or DNS record per app.

### Phase 2 cost drivers

These are approximate list prices from memory, to be priced properly when Phase 2 is scoped.

| Driver | Why it appears | Rough monthly size per cell |
| --- | --- | --- |
| Application Load Balancer | Every cell has its own | About $16 plus capacity units |
| Two larger tasks | Two zones and headroom: 0.5 vCPU and 1 GB each | About $29 |
| Private egress | A NAT gateway at about $33 each plus data, or interface endpoints at about $7 per endpoint per zone | $35 to $110 |
| DynamoDB session table | A read on every request, a write on login and refresh | Single-digit dollars at thousands of users |
| KMS key | Protects the shared key ring and the table | About $1 plus requests |
| Logs and audit export | ALB access logs, audit events, forwarding to the security tooling | Driven by volume |
