# What AWS's testing policy permits for a penetration test and a load test, under the managed experience

- **Ticket:** #1252, "What AWS's testing policy permits for a penetration test and a load test,
  under the managed experience" (`wayfinder:research`), on the map #1251.
- **Date:** 2026-11-16.
- **Branch:** `research/aws-testing-policy`, pushed and never merged, as the map rules.
- **Method:** docs-only. Every policy sentence quoted below, except where marked carried, was read
  on 2026-11-16 from the page that owns it, downloaded over plain HTTPS with no credential and reduced to text: AWS's
  penetration testing policy, the Amazon EC2 testing policy (the page the penetration policy calls
  the "Stress Test policy"), the DDoS simulation testing policy, the AWS Acceptable Use Policy, the
  AWS Service Terms, the knowledge-centre article on security assessments, the Account Management
  guide's pages for the new AWS experience (managed policies, supported services, spend limits),
  the Amazon Cognito quotas page, and Ping Identity's subscription agreement. **Prices** are the
  US East (Ohio), `us-east-2`, rates from AWS's public price-list offer files, downloaded
  2026-11-16; two billing rules (the load balancer capacity unit, standard Parameter Store
  parameters) come from the marketing price pages, which state rules but do not render a Region
  table to a text fetch. Repository reading at `adf0273` (origin/main on 2026-11-16):
  `docs/agents/aws-rules.md`, `tools/aws-project.env` (the Region name alone),
  `infra/modules/cell` (`alb.tf`, `ecs.tf`, `network.tf`), `infra/modules/idp-cognito/main.tf`,
  `contracts/upstream-contract.md`, `docs/plan/06-phase-2-and-risks.md`, and two earlier findings
  on their branches (`research/pingfederate-evaluation-licence-image-and-config`,
  `research/p2-7-stand-in-costs`). **Not done:** no `aws` call, no `terraform` command, no
  credential step, no read of the cell; the `aws-mcp` connector failed to connect. So nothing here
  was tested against the project: whether a spend limit is set and at what value, whether the
  Simulated Events form opens for this project, and the project's plan (Free or Paid) are all
  **unverified**. Where a page did not say something, this file says "not found" and does not fill
  the gap. Readings that are this file's inference and not a page's words are labelled
  **inference**.

## Answer in brief

1. **A penetration test of the gateway, through the internal ALB, on ECS and Fargate, needs no
   approval and no notice.** "Elastic Load Balancers", "Amazon Elastic Container Service" and "AWS
   Fargate" are on the Permitted Services list, as are NAT Gateways and WAF. The tooling may run
   inside AWS. A test that hosts command and control needs prior approval.
2. **S3, DynamoDB, KMS, Systems Manager (Parameter Store) and Cognito are not on the list.** The
   page's rule for them: "Customers seeking to test non approved services will need to work directly
   with AWS Support or your account representative." Assessing "the AWS services themselves" is not
   permitted at all. **Inference:** testing the gateway's own behaviour, which reads S3 and
   DynamoDB as a side effect, is a test of the listed services; aiming a scanner or fuzzer at the
   S3, DynamoDB, KMS, Systems Manager or Cognito endpoints, including the Cognito managed login
   domain, is not covered and needs AWS Support first.
3. **Forbidden in a penetration test, whatever the target:** denial of service, real or simulated,
   "Port flooding", "Protocol flooding", "Request flooding (login request flooding, API request
   flooding)", "S3 bucket takeover", "Subdomain Takeover", and the three Route 53 DNS activities. A
   tool with a denial-of-service capability must have it disabled, and the customer must verify
   that before the test. A tool that "creates, determines the existence of, or demonstrates a DoS
   condition" is "expressly forbidden"; crashing one process during exploitation is allowed.
4. **A load test has no numeric notice threshold on the pages read.** The line between a load test
   and a simulated DDoS is purpose, not volume: a network stress test "sends a large volume of
   legitimate or test traffic to a specific intended target application", where "The endpoint and
   infrastructure are expected to be able to handle this traffic"; a test that "purposefully attempt[s] to overwhelm the target" by
   flooding is a DDoS test. The only numbers: AWS "may employ traffic engineering or traffic
   shaping" where "traffic surges exceed 25Gbps or over 100Gbps", and endpoints hosted in AWS
   should be "in the local AWS Region". The project's load test, two tasks at 0.25 vCPU, is many
   orders of magnitude below that, so **no notice is needed**. The 20 Gbps, 50,000 packets a second
   and 50,000 requests a second figures are caps on an approved DDoS simulation, not load-test
   thresholds.
5. **A DDoS simulation is out of reach for this project.** It needs a pre-approved partner and a
   target under AWS Shield Advanced, and the supported-services page says "AWS Shield is not
   supported" under the new experience.
6. **One load-test measure sits on the forbidden side when run on AWS, by a cautious reading; no
   page states it:** driving one app's traffic until it "exhausts the upstream connection bound"
   is demonstrating a denial-of-service condition in the policy's words. Against that reading: the
   bound is a small per-app constant, so reaching it takes no flood. The safe course is to run
   that probe in the local Docker tier, which is not AWS, and hold the deployed sitting to a rate
   the cell is expected to serve.
7. **Three notice periods are published, and they disagree:** "at least two (2) weeks" for a
   Simulated Events request (penetration testing page), "at least 14 business days" for a stress
   test over the policy's limits (knowledge centre), and "at least 14 days" for a DDoS simulation
   exception. None applies to the two activities the map plans.
8. **The managed experience adds no testing rule that was found, and two practical limits.** The
   managed policies, supported-services and spend-limit pages say nothing about penetration or load
   testing. The limits: Shield is unsupported (point 5); and a spend limit, if one is set, denies
   creating a service, a load balancer, a table, a key or a user pool "about 7 days before you
   would reach your limit" when that control is on, and at the limit "pauses your project and stops
   all resources". The minimum limit is "the greater of $20 or a conservative estimate of your
   likely spend". A one-hour load test at an assumed 200 requests a second adds about **$0.88**
   (section 3.3), so the load test does not trip a limit; a sitting that begins near the limit
   fails at apply.
9. **PingFederate under the evaluation licence may be the target, with one hard rule on
   publishing.** Nothing in Ping's subscription agreement forbids testing or loading the
   self-hosted software. Clause 12.15 does forbid disclosing the outcome: "Customer shall not
   publish or disclose to any third party any opinions relating to, or test results, benchmarking
   or comparative study involving any Product without the prior written consent of Ping Identity."
   This repository is public (`gh repo view --json visibility`, 2026-11-16), so a PingFederate latency or throughput
   figure in an issue, a finding or `tools/bench/results` would be disclosure. The trial terms
   still hold: test or mock data only, not production.
10. **Cognito may carry sign-in load only inside its quotas, and is not a penetration target.** Its
    Service Terms section says nothing about testing. Its quotas: 120 requests a second for the
    `UserAuthentication` category across the account in the Region, and for a user pool domain 300
    a second from one IP address, 300 a second for one app client, 500 a second overall, none of
    the three adjustable. Login request flooding is forbidden (point 3). So a load test signs in a
    fixed set of test users once and replays sessions; it does not hammer sign-in.

## 1. The penetration testing policy

Source: [pen]. The page carries no date; its footer reads 2026.

### 1.1 What is permitted without approval

"AWS customers are welcome to carry out security assessments or penetration tests of their AWS
infrastructure without prior approval for the services listed in the next section under 'Permitted
Services.' Additionally, AWS permits customers to host their security assessment tooling within the
AWS IP space or other cloud provider for on-prem, in AWS, or third party contracted testing. All
security testing that includes Command and Control (C2) requires prior approval."

The Permitted Services list, in full, in the page's words:

- Amazon EC2 instances, WAF, NAT Gateways, and Elastic Load Balancers
- Amazon RDS
- Amazon CloudFront
- Amazon Aurora
- Amazon API Gateways
- AWS AppSync
- AWS Lambda and Lambda Edge functions
- Amazon Lightsail resources
- Amazon Elastic Beanstalk environments
- Amazon Elastic Container Service
- AWS Fargate
- Amazon OpenSearch Service
- Amazon FSx
- Amazon Transit Gateway
- Amazon Bedrock AgentCore
- AWS Global Accelerator

"Customers seeking to test non approved services will need to work directly with AWS Support or your
account representative."

What "security assessment" covers: "port-scanning, vulnerability scanning/checks, penetration
testing, exploitation, web application scanning, as well as any injection, forgery, or fuzzing
activity, either performed remotely against your AWS assets, amongst/between your AWS assets, or
locally within the virtualized assets themselves." And: "You are NOT limited in your selection of
tools or services".

### 1.2 The cell's services against the list

| Service in the cell | On the Permitted Services list | Reading |
| --- | --- | --- |
| Internal Application Load Balancer (`infra/modules/cell/alb.tf`, `internal = true`) | Yes: "Elastic Load Balancers". The page does not distinguish internal from internet-facing. | No approval. The tool must run where it can reach the load balancer, which the page allows ("within the AWS IP space"). |
| ECS on Fargate (the gateway tasks) | Yes: "Amazon Elastic Container Service", "AWS Fargate" | No approval |
| NAT gateway (the multi-task cell's egress) | Yes: "NAT Gateways" | No approval |
| S3 | **No** | "work directly with AWS Support" |
| DynamoDB | **No** | "work directly with AWS Support" |
| KMS | **No** | "work directly with AWS Support" |
| Parameter Store (AWS Systems Manager) | **No** | "work directly with AWS Support" |
| Cognito | **No** | "work directly with AWS Support" |

Above all of these: "Customers are not permitted to conduct any security assessments of AWS
infrastructure or the AWS services themselves."

**Inference, not the page's words.** The page lists services, not kinds of request, and does not
say what counts as "testing" a service that is not listed. The reading this finding recommends:

- Requests sent to the gateway through the load balancer are a test of the load balancer, ECS and
  Fargate, all listed, even though the gateway then reads S3, DynamoDB and Parameter Store with its
  own task role. The project's assets are the target; the unlisted services see ordinary,
  authenticated use.
- Reading the project's own configuration with its own credentials (a bucket policy, a key policy,
  a table's settings) is review of the project's settings, not a penetration test of the service.
- A scanner, fuzzer or exploit aimed at an S3, DynamoDB, KMS, Systems Manager or Cognito endpoint,
  including the Cognito managed login domain and its token endpoint, is a test of an unlisted
  service. It needs AWS Support's answer first, and may be refused as an assessment of the service
  itself.

If the spec wants the unlisted five tested beyond that, the question goes to AWS Support before the
sitting; no page read gives a lead time for that request.

### 1.3 What is prohibited

The Prohibited Activities list, in the page's words:

- DNS zone walking via Amazon Route 53 Hosted Zones
- DNS hijacking via Route 53
- DNS Pharming via Route 53
- Denial of Service (DoS), Distributed Denial of Service (DDoS), Simulated DoS, Simulated DDoS
  (These are subject to the DDoS Simulation Testing policy)
- Port flooding
- Protocol flooding
- Request flooding (login request flooding, API request flooding)
- S3 bucket takeover
- Subdomain Takeover

"Prohibited Services for Outbound Penetration Testing": Amazon API Gateway and Amazon Bedrock
AgentCore. The cell uses neither.

On tools: "you ARE prohibited from utilizing any tools or services in a manner that perform
Denial-of-Service (DoS) attacks or simulations of such against ANY AWS asset, yours or otherwise."
Two carve-outs: banner grabbing "for the purpose of comparison to a list of versions known to be
vulnerable to DoS, is NOT in violation", and a tool "that solely crashes a running process on your
AWS asset, temporary or otherwise, as necessary for remote or local exploitation ... is NOT in
violation", though it "may NOT engage in protocol flooding or resource request flooding". Then: "A
security tool or service that creates, determines the existence of, or demonstrates a DoS condition
in ANY other manner, actual or simulated, is expressly forbidden." A tool with a denial-of-service
capability "must have the explicit ability to DISABLE, DISARM, or otherwise render HARMLESS, that
DoS capability. Otherwise, that tool or service may NOT be employed for ANY facet of the security
assessment." The customer must "independently validate that the tool or service employed does not
perform DoS attacks, or simulations of such, PRIOR to security assessment of any AWS assets."

For the spec this means the tool's ticket names the scanner's denial-of-service checks and the
setting that turns them off, and the sitting records that setting before the scan starts.

### 1.4 What needs a Simulated Events form

A form, "at least two (2) weeks in advance of the start date", with "dates, account ID's involved,
assets involved, and contact information, including phone number and detailed description of planned
events", answered by "a non-automated response ... within 2 business days confirming receipt":

- "covert adversarial security simulations and/or hosting Command and Control (C2)"
- iPerf testing
- simulated phishing
- malware testing

The form's address is `https://console.aws.amazon.com/support/contacts#/simulated-events`, inside
the console. Whether it opens for a project under the new experience: unverified.

### 1.5 Obligations that hold even with no approval

- Security testing "Will be limited to the services, network bandwidth, requests per minute, and
  instance type" and is subject to the Customer Agreement.
- "Any discoveries of vulnerabilities or other issues that are the direct result of AWS's tools or
  services must be conveyed to AWS Security within 24 hours of completion of testing"
  (`aws-security@amazon.com`).
- "If AWS receives an abuse report for activities related to your security testing, we will forward
  it to you. When responding, please provide us with approved language detailing your use case,
  including a point of contact".
- "you are responsible for any damages to AWS or other AWS customers that are caused by your testing
  or security assessment activities."

The knowledge-centre article [kc] restates the first rule: "You don't need approval from AWS to run
penetration tests against or from the designated resources on your AWS account."

## 2. The stress and load testing policy

### 2.1 What the policy says

The penetration testing page defines it: "Network Stress Testing or Load testing is a performance
test that sends a large volume of legitimate or test traffic to a specific intended target
application to ensure efficient operational capacity. Customers wishing to perform a Network Stress
Test should review our Stress Test policy." That link is the Amazon EC2 Testing Policy [ec2test],
whose whole text is three paragraphs:

- Scope: "customers who are planning on running high volume network tests directly from their Amazon
  EC2 instances to other locations such as other Amazon EC2 instances, AWS properties/services, or
  external endpoints". "These tests are sometimes called stress tests, load tests, or gameday
  tests."
- The definition: "we consider a 'network stress test' to be when a test sends a large volume of
  legitimate or test traffic to a specific intended target application. The endpoint and
  infrastructure are expected to be able to handle this traffic".
- The line: "Tests that purposefully attempt to overwhelm the target and/or infrastructure with
  packet or connection flooding attacks, reflection/amplification attacks or other large volumes of
  traffic are not considered network stress tests but are considered distributed denial of service
  (DDoS) tests. Volumetric network-based DDoS simulations are explicitly prohibited from the Amazon
  EC2 platform".
- The numbers: "you should ensure that your endpoints are in the local AWS Region if they are hosted
  within AWS. AWS may employ traffic engineering or traffic shaping operations in some cases where
  traffic surges exceed 25Gbps or over 100Gbps depending on the network paths and AWS Regions
  involved. AWS reserves the right to limit your network traffic if it violates the AWS Acceptable
  Use Policy".

**There is no threshold on this page above which a load test needs notice.** The page has no
gigabit-per-second, packet-rate or duration figure that turns a test into a notifiable event; 25 and
100 Gbps are where AWS may shape traffic. The knowledge-centre article [kc] says: "If your planned
tests exceed the limits in the policy, then submit a request through the Simulated Event submission
form. Important: Submit the simulated event request at least 14 business days before your planned
test." The only "limits in the policy" as it reads today are the 25 and 100 Gbps figures and the
same-Region rule; which the article means is not stated.

Not stated either: whether the policy reaches a load generator that is a Fargate task, or one
outside AWS. Its scope sentence names EC2 instances. The cell's load balancer is internal, so the
generator has to run in the cell's VPC; a Fargate task there is the nearest case to the one the page
names, and this finding treats the policy as applying to it (**inference**).

The Acceptable Use Policy [aup] ("Last Updated: July 1, 2021") is the backstop: no use "to violate
the security, integrity, or availability of any user, network, computer or communications system,
software application, or network or computing device". The Service Terms [terms] add clause 1.8:
"You may perform benchmarks or comparative tests or evaluations (each, a 'Benchmark') of the
Services. If you perform or disclose ... any Benchmark of any of the Services, you (i) will include
in any disclosure, and will disclose to us, all information necessary to replicate such Benchmark".
A published load-test figure for the cell therefore carries what is needed to reproduce it, which
the measure-then-budget method already records. No service section of the terms for a service the
cell uses mentions load or penetration testing; the one that does is AppSync's, which the cell does
not use.

### 2.2 The DDoS simulation policy, for the boundary

Source: [ddos]. "DDoS simulation testing is permitted on AWS, subject to the following Terms and
Conditions":

- It "must be performed by an AWS Partner Network (APN) Partner that has been pre-approved by AWS".
  The four named: NCC Group plc, RedWolf Security Incorporated, Red Button, Safedash Analytics.
- The target "must be either registered as a Protected Resource in an AWS account you own that is
  subscribed to AWS Shield Advanced or an Amazon API Gateway edge-optimized API endpoint that
  resides in an account you own subscribed to AWS Shield Advanced."
- "The bit volume of the DDoS simulation test may not exceed 20 gigabits per second."
- "The packet volume ... may not exceed 5 million packets per second when testing an Amazon
  CloudFront distribution and may not exceed 50,000 packets per second when testing any other type
  of AWS resource."
- "The request volume of the DDoS simulation test may not exceed 50,000 requests per second."
- It "may not originate from an AWS resource".
- Exceptions, or a vendor that is not an approved partner: "submitting the form at least 14 days
  before the proposed test date".

These are caps on a test that is already a DDoS simulation. They do not say that a load test under
50,000 requests a second is not one: purpose decides, per section 2.1.

### 2.3 What that means for the map's load test

The map's deployed tier is one sitting on Fargate with two tasks at 0.25 vCPU, measuring requests a
second per task, p99 latency, memory and the cost of reading S3. Against the pages:

- **Permitted without notice:** sending test traffic at a rate the cell is expected to serve, from a
  generator in the same Region, to measure capacity and latency. Volume is nowhere near 25 Gbps.
- **On the forbidden side, by inference:** a run whose aim is to exhaust something. The map's last
  measure, "whether one app's traffic exhausts the upstream connection bound"
  (`contracts/upstream-contract.md`), is by design an attempt to show that one tenant can starve
  the rest. On AWS that is a test that "purposefully attempt[s] to overwhelm the target" with
  "connection flooding", and a tool that "demonstrates a DoS condition". The pages give no
  exemption for a small one against one's own task. Against this reading: the bound is a small
  per-app constant, reached by holding connections and not by a flood, and the policy's examples
  are volumetric; no page settles it. The local tier (Docker at 0.25 vCPU and 1 GB
  against the stand-ins) is outside AWS and outside these policies, and it holds the same bound, a
  constant in `Platform.Proxy`. Recommendation: the exhaustion probe is a local-tier measure; the
  deployed sitting ramps to a stated rate and stops at the first sustained error or latency
  breach, not at collapse.
- **Sign-in under load:** section 4.2.

## 3. The new AWS experience

### 3.1 Whether it adds a testing rule

Not found. The managed policies page [scp], the supported-services page [supported], the sign-up
comparison [signup] and the spend-limit page [spend] contain no occurrence of "penetration",
"simulated", "stress" or "load test". `docs/agents/aws-rules.md` has none either. So the general
policies of sections 1 and 2 are the only rules read; this means none was found in the pages read, not
that AWS has none.

What the experience does change:

- **AWS Shield is not supported.** The Free Tier list's AWS WAF entry carries the note "AWS
  Shield is not supported.", and AWS Shield is on the list headed "AWS services not supported for our
  new AWS experience" [supported]. A DDoS simulation needs a Shield Advanced subscription (section 2.2), so it would need "Activate advanced
  AWS features" first. The map plans none.
- **AWS Support is on the Free Tier list**, and `support:*` is among the actions the Free Tier and
  Paid Plan service policies exempt from their deny (`AllowFreeTierServices`,
  `AllowPaidTierServices`) [scp], so the route for an unlisted service ("work directly with AWS Support") is not
  closed by policy. Whether a support case of that kind is answered on the project's support plan:
  unverified.
- **One Region.** The stress-test page's same-Region rule is met by construction: the cell and any
  generator are in `us-east-2` (`tools/aws-project.env`).

### 3.2 The spend limit's mechanism

Source: [spend], [scp]. Whether this project has a spend limit, its value and its early controls
are unverified: reading them is a credentialed step in AWS Settings.

- "If your usage in a project reaches its limit, AWS pauses that project which stops its resources".
  "AWS pauses your project and stops all resources. Your data is preserved." Reactivation is by
  raising the limit; "some resources may need to be manually restarted"; after 90 days paused the
  project's data is deleted.
- "the minimum allowed value is the greater of $20 or a conservative estimate of your likely
  spend". It is a monthly, pre-tax figure and excludes credits.
- Notifications at 50%, 75% and 90%, "or when you are on track to reach or exceed your spend limit
  in the next 10 days."
- Optional early controls. "Stop new resource creation": "About 7 days before you would reach your
  limit, new resources stop launching", by a service control policy. The spend-limit policies deny,
  among others, `ecs:CreateService`, `elasticloadbalancing:CreateLoadBalancer`,
  `ec2:CreateNatGateway`, `ec2:RunInstances`, `dynamodb:CreateTable`, `kms:CreateKey`,
  `cognito-idp:CreateUserPool`, `secretsmanager:CreateSecret` and `ecr:CreateRepository`.
  `ecs:RunTask` is not in the list. "Pause idle resources" (about 5 days before) and "Pause top cost
  drivers" (about 4 days before) act on EC2, RDS, SageMaker, Lambda and Bedrock; `infra/`
  declares no instance, database, function or endpoint of those services.

Consequence for a sitting: both early denial and the pause are driven by the month's projected and
actual spend, not by request rate. A load test trips the limit only by what it costs.

### 3.3 What a load test costs, against the limit

Rates, `us-east-2`, from the offer files (section 6 gives each file's version):

| Item | Rate | Source |
| --- | --- | --- |
| Fargate ARM vCPU | $0.03238 per vCPU-hour | [o-ecs] |
| Fargate ARM memory | $0.00356 per GB-hour | [o-ecs] |
| Application Load Balancer | $0.0225 per hour | [o-elb] |
| Load balancer capacity unit (LCU) | $0.008 per LCU-hour | [o-elb] |
| S3 GET | $0.0004 per 1,000 requests | [o-s3] |
| DynamoDB on-demand read | $0.125 per million read request units | [o-ddb] |
| DynamoDB on-demand write | $0.625 per million write request units | [o-ddb] |
| KMS request | $0.03 per 10,000 requests, after 20,000 free a month | [o-kms] |
| CloudWatch Logs ingestion, Standard class | $0.50 per GB | [o-cw] |
| NAT gateway | $0.045 per hour, $0.045 per GB processed | [o-ec2] |
| Public IPv4 address | $0.005 per hour | [o-vpc] |
| Data transfer between zones | $0.010 per GB | [o-dt] |
| Data transfer out to the internet | $0.090 per GB, first 10 TB a month | [o-dt] |
| Parameter Store, standard parameter, standard throughput | "No additional charge" | [p-ssm] |
| Cognito, Essentials tier (`user_pool_tier = "ESSENTIALS"`) | $0.015 per monthly active user; the offer file lists a free band of 10,000 | [o-cog] |
| Cognito quota increase, `UserAuthentication` | $20 per added request-a-second for a full month, $45 for a partial month | [o-cog] |

"You are charged only on the dimension with the highest usage. An LCU contains: 25 new connections
per second. 3,000 active connections per minute ... 1 GB per hour for Amazon Elastic Compute Cloud
(EC2) instances, containers, and IP addresses as targets ... 1,000 rule evaluations per second"
[p-elb]. The table below takes processed bytes as the highest dimension, an assumption.

**Assumptions, all this file's and none measured:** one hour at a steady 200 requests a second
(720,000 requests), half assets and half API calls; one S3 GET per asset request; one DynamoDB read
unit per API call and one write unit per ten; a 20 KB mean response; 1 KB of gateway log per
request; a generator of one Fargate ARM task at 1 vCPU and 2 GB, started with `RunTask` inside the
cell's VPC and gone when it exits; S3 and DynamoDB reached through the gateway endpoints the
multi-task cell has (`infra/modules/cell/network.tf`), logs through the NAT gateway.

| What the load test adds, per sitting-hour | Arithmetic | Cost |
| --- | --- | --- |
| Generator task | 1 x $0.03238 + 2 x $0.00356 | $0.040 |
| S3 GET | 360,000 x $0.0004 / 1,000 | $0.144 |
| DynamoDB reads | 360,000 x $0.125 / 1,000,000 | $0.045 |
| DynamoDB writes | 36,000 x $0.625 / 1,000,000 | $0.023 |
| Load balancer capacity | 14.4 GB an hour = 14.4 LCU x $0.008 | $0.115 |
| Log ingestion | 0.69 GB x $0.50 | $0.343 |
| NAT processing of the logs | 0.69 GB x $0.045 | $0.031 |
| Cross-zone transfer, worst case all of it | 14.4 GB x $0.010 | $0.144 |
| **Total** | | **about $0.88** |

- **Per sitting-hour:** about $0.88 at 200 requests a second; the request-driven lines scale with
  the rate, so about $4.25 at 1,000 a second. Log ingestion is the largest line and the one a
  quieter log level cuts.
- **Per idle month:** $0 under the map's cost rule, since the generator is a task that exits and
  nothing else is created for the test. Left running for 730 hours the generator alone is $28.84,
  and $32.49 with a public address. Stored log data is not priced here.
- **Not in the total:** the standing cell (two gateway tasks at 0.25 vCPU and 1 GB are $0.0233 an
  hour together, the load balancer $0.0225, the NAT gateway $0.045), which the sitting pays whether
  or not it load-tests; and KMS, because whether the gateway calls KMS per request was not
  established. If it did, once per request, 720,000 calls would add $2.16 an hour and become the
  largest line; that is a question for the load test's own ticket.
- **Against a spend limit:** at the minimum limit of $20 the assumed test would have to run for
  more than twenty hours to consume it. The risk is the other one: a sitting whose apply lands
  after "Stop new resource creation" has engaged cannot create the service, the load balancer or
  the table.

## 4. The identity providers as targets

### 4.1 PingFederate under the evaluation licence

The licence terms were established by the finding on the branch
`research/pingfederate-evaluation-licence-image-and-config` (read 2026-11-04, **carried, not
re-read today**): the DevOps evaluation licence is "short-lived (30 days) and must not be used in
production deployments" and works only with images published in the last 90 days; by accepting the
EULA the software is a "Trial Product" under Ping's subscription agreement.

The agreement itself [ping] ("Effective May 9, 2025") was re-read today for testing terms:

- **Which terms reach self-hosted PingFederate.** "'Products' means the Software and Service."
  "'Service' means hosted, software-as-a-service offerings provided by Ping Identity". "'Software'
  means the downloadable software programs offered by Ping Identity that are ... downloaded or
  installed by Customer". The PingFederate container is Software, not the Service.
- **Clause 2.1, Restrictions.** Those about interference and unauthorized access are written for
  the hosted Service: "(vii) interfere with or disrupt the integrity or performance of the Service
  or the data contained therein", "(viii) attempt to gain unauthorized access to the Service or its
  related systems or networks". Those that reach the Software: "(iii) reverse engineer, decompile
  or disassemble the Products", and "(ix) circumvent, disable, or interfere with security-related
  features of the Products or Deliverables, or features that enforce limitations on use of the
  Products". No clause names load testing, penetration testing or security scanning of the
  customer's own installation. **Inference:** black-box testing of the gateway's flows against the
  project's own PingFederate container, and load on it, are not restricted; decompiling it, or
  defeating its licence enforcement, is.
- **Clause 12.10, Trial Products.** "Customer is not permitted to use Trial Products in connection
  with Personal Information. Customer represents and warrants that it will only use test or mock
  data with Trial Products." The trial is "AS IS" and may be ended "by Ping Identity in its sole
  discretion".
- **Clause 12.15, Marketing and Publicity.** "Customer shall not publish or disclose to any third
  party any opinions relating to, or test results, benchmarking or comparative study involving any
  Product without the prior written consent of Ping Identity." This is the term with a consequence
  here. This repository is public (`gh repo view --json visibility` returned `PUBLIC` on
  2026-11-16). A PingFederate response time, throughput figure, scan result or comparison
  with Cognito written to an issue, a finding, `docs/gaps.md` or `tools/bench/results` is published
  to third parties. The spec either keeps Ping-specific results out of the repository and its
  tracker, reports the gateway's own numbers with the provider's share removed, or obtains Ping's
  written consent first. Which of these is a decision for Adam.
- Also clause 2.1: "certain Services have rate limiting in effect for the protection of the
  Service". That sentence is about the hosted Service. A rate limit or request cap in the evaluation
  licence for the self-hosted software: not found (the earlier finding also records the licence's
  feature set as not found).

PingFederate runs as a container in the cell, on ECS and Fargate, both listed in section 1.1, so
AWS's policy treats a test of it as a test of the project's own workload.

### 4.2 Cognito

- **Service Terms** [terms], section 27, has two clauses, on third-party identity providers and on
  deleting a user pool with no active users in 12 months. Neither mentions testing or load.
- **Penetration testing:** Cognito is not on the Permitted Services list (section 1.2). Its managed
  login pages, authorize and token endpoints are AWS's service, not the project's asset. A scan of
  them needs AWS Support's answer first.
- **Load:** the quotas page [cogquota] is the governing limit. "Operation quotas are defined as the
  maximum number of requests per second (RPS) for all operations within a category", measured
  across "all user pools ... in your AWS account in one Region". `UserAuthentication`, which covers
  `InitiateAuth`, token refresh "with InitiateAuth or the token endpoint" and "Managed login or
  classic hosted UI sign-in", defaults to 120 a second and is adjustable, at the prices in section
  3.3. Per user: 10 a second. "Bulk request-rate limits for user pool domains", none adjustable:
  300 a second from one source IP address to one domain, 300 a second for one app client ID, 500 a
  second for one domain, 50,000 a second for `jwks.json` across the account in the Region. Over a
  quota, Cognito throttles.
- A generator behind the cell's NAT gateway is one source address, so its ceiling against the
  managed login domain is 300 a second whatever the category quota (**inference** from the quota's
  wording).
- **Billing:** a signed-in test user is a monthly active user at the Essentials rate (section 3.3).
- **The rule that binds:** "Request flooding (login request flooding, API request flooding)" is a
  prohibited activity (section 1.3). A load test that needs sessions signs a fixed set of test
  users in once, below the quotas, and replays the sessions; the sign-in path's own throughput is
  measured against the local stand-in, not against Cognito.

## 5. The table

"Needs notice" means AWS (or the vendor) must agree first. Each row's source is the section above
and the page it cites.

| Activity | Permitted without notice | Needs notice | Forbidden | Source |
| --- | --- | --- | --- | --- |
| Scanning, fuzzing, injection and exploitation of the gateway through the internal ALB, on ECS and Fargate, with tooling inside the VPC | Yes | | | [pen], Permitted Services |
| The same against the PingFederate container in the cell | Yes, under AWS's policy; Ping's terms do not restrict it (inference) | | Decompiling it or defeating licence enforcement | [pen]; [ping] 2.1 |
| Scanning or attacking the S3, DynamoDB, KMS, Systems Manager or Cognito endpoints | | Yes: "work directly with AWS Support"; no lead time found | Assessing "the AWS services themselves" | [pen] |
| Reviewing the project's own bucket, key, table and parameter settings with its own credentials | Yes (inference: configuration review, not a penetration test) | | | [pen] |
| Command and control hosting, covert red-team simulation | | Simulated Events form, two weeks ahead | | [pen] |
| iPerf, simulated phishing, malware testing | | Simulated Events form, two weeks ahead | | [pen] |
| Denial of service, real or simulated; port, protocol or request flooding, including login and API request flooding | | | Yes | [pen], Prohibited Activities |
| A tool with a denial-of-service capability left enabled | | | Yes | [pen], tools policy |
| S3 bucket takeover, subdomain takeover, Route 53 zone walking, hijacking, pharming | | | Yes | [pen] |
| Load test: test traffic at a rate the cell is expected to serve, generator in the same Region, far below 25 Gbps | Yes | | | [ec2test] |
| Load test above "the limits in the policy" | | Simulated Events form, 14 business days ahead; the limits are not numbered beyond 25 and 100 Gbps | | [kc], [ec2test] |
| Driving traffic to exhaust the upstream connection bound or the task, on AWS | | | Yes, as a demonstrated denial-of-service condition (inference); run it in the local tier | [pen], [ec2test] |
| DDoS simulation | | An approved partner, a Shield Advanced target; otherwise a form 14 days ahead | Unavailable here: Shield is not supported under the experience | [ddos], [supported] |
| Sign-in load on Cognito within its quotas (120 a second for the category; 300, 300 and 500 a second for a domain) | Yes | A quota increase, priced, through Service Quotas | Login request flooding | [cogquota], [pen] |
| Load on the PingFederate container | Yes (no restriction found) | | Production use; anything but test or mock data | [ping] 12.10 |
| Publishing a test result, benchmark or comparison involving PingFederate in this public repository or its tracker | | Ping Identity's prior written consent | Without that consent | [ping] 12.15 |
| Publishing a benchmark of an AWS service | Yes, with "all information necessary to replicate such Benchmark" | | | [terms] 1.8 |
| Reporting a flaw found in an AWS service | | To AWS Security within 24 hours of the test's end | | [pen] |

## 6. What was executed and what was only read

**Executed:** downloads over HTTPS with no credential and no account detail in any request; text
extraction and price-file parsing with `python3 -I`; `git` and `gh` reads of this repository. No
`aws` CLI call, no `terraform` command, no container, no request to the cell or to any Cognito or
Ping endpoint of the project. Nothing in this file was tested.

**Read on 2026-11-16, each from its owner's page:**

- [pen] AWS, "Penetration Testing" (the customer support policy, permitted services, prohibited
  activities, other simulated events, terms, tools policy):
  <https://aws.amazon.com/security/penetration-testing/>. No date on the page.
- [ec2test] AWS, "Amazon EC2 Testing Policy", the network stress test policy:
  <https://aws.amazon.com/ec2/testing/>. No date on the page.
- [ddos] AWS, "DDoS Simulation Testing Policy":
  <https://aws.amazon.com/security/ddos-simulation-testing/>. No date on the page.
- [kc] AWS re:Post knowledge centre, "How do I run security assessments or penetration tests on
  AWS?": <https://repost.aws/knowledge-center/penetration-testing>. A reader comment under the
  article says its approval sentence contradicts the policy page; the comment is not AWS's text and
  is not relied on.
- [aup] AWS Acceptable Use Policy, "Last Updated: July 1, 2021": <https://aws.amazon.com/aup/>.
- [terms] AWS Service Terms, clause 1.8 and section 27 (Amazon Cognito); searched for "load test",
  "stress", "penetration", "benchmark": <https://aws.amazon.com/service-terms/>.
- [scp] AWS Account Management, "Managed policies for your organization", including "Service
  control policies for spend limits":
  <https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html>.
- [supported] AWS Account Management, supported services for the new experience:
  <https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html>.
- [signup] AWS Account Management, "Compare sign-up options":
  <https://docs.aws.amazon.com/accounts/latest/reference/sign-up-for-aws.html>.
- [spend] AWS Account Management, "Create a spend limit in AWS Settings":
  <https://docs.aws.amazon.com/accounts/latest/reference/create-spend-limit.html>.
- [cogquota] Amazon Cognito developer guide, "Quotas in Amazon Cognito":
  <https://docs.aws.amazon.com/cognito/latest/developerguide/quotas.html>.
- [ping] Ping Identity, Subscription Agreement, "Effective May 9, 2025", definitions and clauses
  2.1, 12.10 and 12.15: <https://www.pingidentity.com/en/legal/subscription-agreement.html>.
- [p-elb] Elastic Load Balancing pricing, for the LCU's dimensions only:
  <https://aws.amazon.com/elasticloadbalancing/pricing/>.
- [p-ssm] AWS Systems Manager pricing, for standard parameters only:
  <https://aws.amazon.com/systems-manager/pricing/>.

**Price-list offer files, `us-east-2`, downloaded 2026-11-16** from
`https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/<offer>/current/us-east-2/index.json`
(`index.csv` for AmazonEC2, read as a stream for its NAT gateway rows only):

- [o-ecs] AmazonECS, version 20261111234623.
- [o-elb] AWSELB, version 20261111153621.
- [o-s3] AmazonS3, version 20261116071300.
- [o-ddb] AmazonDynamoDB, version 20261111153612.
- [o-kms] awskms, version 20261108172342.
- [o-cog] AmazonCognito, version 20261020233814.
- [o-cw] AmazonCloudWatch, version 20261116171036.
- [o-vpc] AmazonVPC, version 20261105181945.
- [o-dt] AWSDataTransfer, version 20261111191941.
- [o-ec2] AmazonEC2, version 20261114012926.

**Carried from earlier findings, not re-read today:** the DevOps evaluation licence's terms (30
days, not for production, images from the last 90 days, "Trial Product" on accepting the EULA), from
`research/pingfederate-evaluation-licence-image-and-config` (read 2026-11-04).

**Not established:**

- Whether the project has a spend limit, its value and which early controls are on; the project's
  plan (Free or Paid). Credentialed reads.
- Whether the Simulated Events form, or a support case about an unlisted service, is available to a
  project under the new experience and on its support plan.
- What "the limits in the policy" means in the knowledge-centre article, and whether the stress-test
  policy reaches a generator that is not an EC2 instance. The pages do not say.
- Where AWS draws the line between testing the project's use of an unlisted service and testing the
  service. The reading in section 1.2 is this file's.
- Whether a small, deliberate exhaustion of the project's own task counts as a simulated denial of
  service. The reading in section 2.3 is the cautious one.
- A lead time for "work directly with AWS Support".
- Whether the evaluation licence caps PingFederate's request rate or features.
- Whether the gateway calls KMS on the request path, and so the KMS line of the load test's cost.
- The date each AWS policy page was last changed: none carries one.
