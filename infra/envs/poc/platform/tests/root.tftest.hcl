# Plans the platform root against a mock AWS provider: no credentials, no backend and no
# certificate in ACM.
#   terraform -chdir=infra/envs/poc/platform init -backend=false
#   terraform -chdir=infra/envs/poc/platform test
#
# Task 1.3's load balancer is what this file checks. Expected values come from the files the
# root answers to where there is one: contracts/resource-tags.md for the tags.

mock_provider "aws" {
  mock_data "aws_availability_zones" {
    defaults = {
      names = ["us-east-1a", "us-east-1b", "us-east-1c"]
    }
  }

  mock_data "aws_acm_certificate" {
    defaults = {
      arn = "arn:aws:acm:us-east-1:111122223333:certificate/00000000-0000-0000-0000-000000000000"
    }
  }

  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }

  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "111122223333"
    }
  }

  # Change request 1.5: the root looks the teardown role up by its derived name. The mock satisfies
  # the lookup, so what this file proves is the name and the wiring; that the role exists is a
  # property of the AWS plan, not of terraform test (ADR-0014 item 2).
  mock_data "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/spa-platform-poc-teardown"
    }
  }

  mock_data "aws_region" {
    defaults = {
      region = "us-east-1"
    }
  }

  mock_data "aws_partition" {
    defaults = {
      partition  = "aws"
      dns_suffix = "amazonaws.com"
    }
  }

  mock_resource "aws_lb" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:loadbalancer/app/spa-poc/0123456789abcdef"
      # A deliberate sentinel: this test proves output wiring, not AWS DNS generation.
      dns_name = "mock-alb-dns-sentinel"
    }
  }

  mock_resource "aws_lb_target_group" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:targetgroup/spa-poc/0123456789abcdef"
    }
  }

  # Task 1.4a. A mock provider generates an opaque id for a computed attribute, and the provider
  # validates the two role ARNs the task definition carries, so those two need ARN-shaped values.
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/spa-platform-poc"
    }
  }

  mock_resource "aws_ecs_cluster" {
    defaults = {
      arn = "arn:aws:ecs:us-east-1:111122223333:cluster/spa-poc"
      id  = "arn:aws:ecs:us-east-1:111122223333:cluster/spa-poc"
    }
  }

  mock_resource "aws_ecs_task_definition" {
    defaults = {
      arn = "arn:aws:ecs:us-east-1:111122223333:task-definition/spa-platform-poc:1"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:us-east-1:111122223333:log-group:/spa-platform/poc/gateway"
    }
  }

  mock_resource "aws_ecr_repository" {
    defaults = {
      arn = "arn:aws:ecr:us-east-1:111122223333:repository/spa-poc"
    }
  }
}

# The apps root's outputs, in the shape infra/envs/poc/apps/outputs.tf gives them: one entry per
# app, keyed by appId, each naming the client id and its secret's parameter but never the secret.
# No value here is read into the task environment; only the two client ids reach the mock API.
override_data {
  target = data.terraform_remote_state.apps
  values = {
    outputs = {
      clients = {
        app1 = {
          clientId        = "1example23456789"
          secretParameter = "/spa-platform/poc/apps/app1/client-secret"
          secretVersion   = 1
        }
        app2 = {
          clientId        = "2example23456789"
          secretParameter = "/spa-platform/poc/apps/app2/client-secret"
          secretVersion   = 1
        }
        app3 = {
          clientId        = "3example23456789"
          secretParameter = "/spa-platform/poc/apps/app3/client-secret"
          secretVersion   = 1
        }
      }
    }
  }
}

variables {
  cell               = "poc"
  vpc_cidr           = "10.20.0.0/16"
  certificate_domain = "spa-poc.test"
  # Read only by the runs against modules/cell, which still takes the list as input. The root
  # derives it (change request 1.5).
  teardown_principal_arns = ["arn:aws:iam::111122223333:role/Admin"]

  # Task 1.4a. The sample image the service starts on, passed by digest like every other image.
  gateway_image = "public.ecr.aws/nginx/nginx@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

  issuers = {
    "cognito-poc" = {
      authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
      profile        = "cognito"
      browser_origin = "https://spa-poc.auth.us-east-1.amazoncognito.com"
    }
  }
}

run "the_root_publishes_the_load_balancer_dns_name" {
  command = apply

  assert {
    condition     = output.alb_dns_name == "mock-alb-dns-sentinel"
    error_message = "The root must publish the ALB resource DNS name as alb_dns_name, the contract's albDnsName."
  }
}

# The module's resources are not addressable from a root test, so these read them through the
# plan of a run against the module itself.
run "the_module_builds_one_listener_and_one_target_group" {
  command = apply

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
  }

  assert {
    condition     = aws_lb.cell.internal && aws_lb.cell.load_balancer_type == "application"
    error_message = "The load balancer must be an internal application load balancer."
  }

  assert {
    condition     = aws_lb.cell.preserve_host_header
    error_message = "The Host header must be preserved: the gateway resolves the app from it (ADR-0010)."
  }

  assert {
    condition     = aws_lb.cell.xff_header_processing_mode == "append"
    error_message = "The ALB must append the client address for the gateway's forward limit of one (ADR-0010, N-09)."
  }

  assert {
    condition     = aws_lb_listener.https.ssl_policy == "ELBSecurityPolicy-TLS13-1-2-2021-06"
    error_message = "The listener must pin the selected TLS 1.2/1.3 policy instead of the older provider default."
  }

  assert {
    condition     = aws_lb.cell.drop_invalid_header_fields
    error_message = "Invalid header fields must be dropped at the load balancer."
  }

  # The plan allowlist further down counts resource types, so it cannot see a block inside aws_lb.
  assert {
    condition     = length(aws_lb.cell.access_logs) == 0
    error_message = "Access logs stay off in Phase 1 (Q8 on #98): an entry records the login callback's query string, authorization code and state included (T13, ADR-0007). modules/cell/alb.tf says why."
  }

  assert {
    condition     = length(aws_lb.cell.subnets) == 2
    error_message = "The load balancer must sit in both of the cell's subnets."
  }

  assert {
    condition     = aws_lb.cell.security_groups == toset([aws_security_group.alb.id])
    error_message = "The load balancer must carry the ALB security group of task 1.2 and no other."
  }

  assert {
    condition     = aws_lb.cell.name == "spa-${var.cell}"
    error_message = "The load balancer is named spa-{cell}, which fits the 32-character limit."
  }

  assert {
    condition     = aws_lb_listener.https.port == 443 && aws_lb_listener.https.protocol == "HTTPS"
    error_message = "The one listener is HTTPS on 443."
  }

  assert {
    condition     = aws_lb_listener.https.certificate_arn == data.aws_acm_certificate.wildcard.arn
    error_message = "The listener must present the imported wildcard certificate."
  }

  assert {
    condition     = data.aws_acm_certificate.wildcard.domain == "*.${var.certificate_domain}" && data.aws_acm_certificate.wildcard.tags["Cell"] == var.cell
    error_message = "The certificate is found by its wildcard domain and this cell's Cell tag, the filter certs.sh uses."
  }

  assert {
    condition     = data.aws_acm_certificate.wildcard.types == tolist(["IMPORTED"])
    error_message = "Only an imported certificate is looked up: certs.sh imports it."
  }

  assert {
    condition     = toset(data.aws_acm_certificate.wildcard.key_types) == toset(split(",", regex("keyTypes=([A-Za-z0-9_,]+)", file("../../../../tools/scripts/certs.sh"))[0]))
    error_message = "The certificate lookup must accept exactly the key types certs.sh searches."
  }

  assert {
    condition = (
      length(aws_lb_listener.https.default_action) == 1
      && aws_lb_listener.https.default_action[0].type == "forward"
      && aws_lb_listener.https.default_action[0].target_group_arn == aws_lb_target_group.gateway.arn
    )
    error_message = "The listener forwards every request to the one target group (ADR-0010)."
  }

  assert {
    condition = (
      aws_lb_target_group.gateway.target_type == "ip"
      && aws_lb_target_group.gateway.protocol == "HTTP"
      && aws_lb_target_group.gateway.port == var.gateway_port
    )
    error_message = "The target group sends HTTP to the gateway's port on a Fargate task's address."
  }

  assert {
    condition     = aws_lb_target_group.gateway.health_check[0].path == "/"
    error_message = "The health-check path defaults to /, for the sample image."
  }
}

# Exercise both the naming boundary and port wiring with non-default inputs. The
# ordinary poc/default-port run cannot detect a long-name or hardcoded-port regression.
run "the_longest_cell_and_a_different_gateway_port" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    cell                  = "abcdefghijklmnopqrstuv"
    gateway_port          = 9090
  }

  assert {
    condition     = aws_lb.cell.name == "spa-${var.cell}" && length(aws_lb.cell.name) <= 32 && aws_lb_target_group.gateway.name == aws_lb.cell.name
    error_message = "Both ALB and target-group names must fit AWS's limit at the cell's maximum length."
  }

  assert {
    condition     = aws_lb_target_group.gateway.port == var.gateway_port
    error_message = "The target group must follow a non-default gateway port."
  }
}

run "the_health_check_path_follows_the_variable" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    health_check_path     = "/readyz"
  }

  assert {
    condition     = aws_lb_target_group.gateway.health_check[0].path == var.health_check_path
    error_message = "The health-check path is the variable: /readyz for the gateway."
  }
}

run "a_health_check_path_must_be_absolute" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    health_check_path     = "readyz"
  }

  expect_failures = [var.health_check_path]
}

run "a_certificate_domain_carries_no_wildcard" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    certificate_domain    = "*.spa-poc.test"
  }

  expect_failures = [var.certificate_domain]
}

run "tags_are_the_ones_the_contract_names" {
  command = plan

  # The first Platform row of the contract is the platform tag; a later one is the bootstrap
  # exemption, which this root must not carry.
  assert {
    condition     = local.tags.Platform == regex("\\| `Platform` \\| `([^`]+)` \\|", file("../../../../contracts/resource-tags.md"))[0]
    error_message = "Platform must be the platform tag in contracts/resource-tags.md: teardown and P-05 match it exactly."
  }

  assert {
    condition     = local.tags.Cell == var.cell
    error_message = "Cell must be the cell name (contracts/resource-tags.md)."
  }

  assert {
    condition     = strcontains(file("../../../../contracts/resource-tags.md"), "| `ManagedBy` | `${local.tags.ManagedBy}`")
    error_message = "ManagedBy must be a value contracts/resource-tags.md names."
  }

  assert {
    condition     = toset(keys(local.tags)) == toset(["Cell", "ManagedBy", "Platform"])
    error_message = "The root applies the contract's three tags and no others."
  }
}

# --- Task 1.4a ---------------------------------------------------------------------------------

# The cell module proves what the ECS resources are (modules/cell/tests/module.tftest.hcl). What is
# this root's own is the four fields it publishes for task 1.7's outputs.json, and the two policy
# bounds it defaults, which have to agree with the configuration contract's own example.
run "the_root_publishes_the_four_fields_task_1_4a_adds_to_the_outputs_contract" {
  command = apply

  assert {
    condition = alltrue([
      for field, value in {
        ecsCluster = output.ecs_cluster
        ecsService = output.ecs_service
        logGroup   = output.log_group
      } : jsondecode(file("../../../../contracts/infra-outputs.example.json"))[field] == value
    ])
    error_message = "ecs_cluster, ecs_service and log_group must be the values contracts/infra-outputs.example.json shows for this cell."
  }

  assert {
    condition     = output.gateway_image == var.gateway_image
    error_message = "gateway_image is the image this apply placed, echoed back so deploy.sh can assert the running digest against it."
  }

  # The contract is additionalProperties: false, so an output with no field in it is a trap for
  # whatever assembles outputs.json. Every output this root publishes has to be one of its fields,
  # under the camelCase spelling the contract uses.
  assert {
    condition = alltrue([
      for name, value in {
        region        = output.region
        cell          = output.cell
        bucket        = output.bucket
        alb_dns_name  = output.alb_dns_name
        ecr_gateway   = output.ecr_gateway
        ecr_mock_api  = output.ecr_mock_api
        ecs_cluster   = output.ecs_cluster
        ecs_service   = output.ecs_service
        log_group     = output.log_group
        gateway_image = output.gateway_image
        } : contains(
        keys(jsondecode(file("../../../../contracts/infra-outputs.schema.json")).properties),
        join("", [for index, part in split("_", name) : index == 0 ? part : title(part)])
      )
    ])
    error_message = "Every output this root publishes must be a field of contracts/infra-outputs.schema.json, in its camelCase spelling."
  }
}

run "the_policy_bounds_this_root_defaults_are_the_contract_s_own_example" {
  command = plan

  # Read from the contract's example rather than restated, so a change there fails here instead of
  # leaving the task environment describing a different proof of concept. These two bound what any
  # registry entry may claim, and a registry writer cannot widen them.
  assert {
    condition     = toset(var.allowed_host_suffixes) == toset(jsondecode(file("../../../../contracts/platform-config.example.json")).Policy.AllowedHostSuffixes)
    error_message = "allowed_host_suffixes must default to contracts/platform-config.example.json -> Policy.AllowedHostSuffixes."
  }

  assert {
    condition     = toset(var.allowed_upstreams) == toset(jsondecode(file("../../../../contracts/platform-config.example.json")).Policy.AllowedUpstreams)
    error_message = "allowed_upstreams must default to contracts/platform-config.example.json -> Policy.AllowedUpstreams."
  }

  # And the pair the apps actually name, which is the same list seen from the other side: the mock
  # API's two listeners are what fixtures/apps/*.json name as their upstreams.
  assert {
    condition = toset(var.allowed_upstreams) == toset(flatten([
      for f in fileset("../../../../fixtures/apps", "*.json") :
      [for api in jsondecode(file("../../../../fixtures/apps/${f}")).apis : api.upstream]
    ]))
    error_message = "allowed_upstreams must be exactly the upstreams the app definitions in fixtures/apps/ name, or an entry names an upstream the policy refuses."
  }
}

run "the_mock_api_s_allowlists_come_from_the_apps_root_s_state" {
  command = plan

  # docs/plan/05-infrastructure-and-cost.md -> Terraform boundaries: this root reads two values from
  # the apps root's state, the client ids of apps 1 and 2. App 3 is not among them, which is what
  # keeps onboarding it out of this root's plan (F-07).
  assert {
    condition = local.mock_api_clients == {
      app1 = "1example23456789"
      app2 = "2example23456789"
    }
    error_message = "The two client ids must come from the apps root's state, not from a value typed into this root."
  }

  # The override above carries a third app, exactly as the apps root's state would once
  # fixtures/onboarding/app3.json is applied. The selection has to leave it out, which is what keeps
  # onboarding app 3 out of this root's plan (F-07) and what makes the mock API refuse its call.
  assert {
    condition     = !strcontains(jsonencode(local.mock_api_clients), local.app_clients["app3"].clientId)
    error_message = "App 3's client must not reach the mock API's allowlists: admission is the API owner's decision (contracts/upstream-contract.md), and F-07 logs app 3 in and still expects a refusal."
  }
}

# What main.tf's try() absorbs (Q4 on #98): an apps root state that exists and carries no clients
# output, which is what a destroyed apps root leaves. Both client ids are then empty, which the
# module accepts while mock_api_image is empty and refuses once it is set, as
# a_mock_api_image_with_no_client_ids_is_refused in modules/cell/tests/module.tftest.hcl shows. A
# missing state is the other half and is not absorbed: the read itself fails the plan, before try()
# runs, and a test that overrides the read cannot reach that failure.
run "an_apps_root_state_with_no_clients_output_leaves_the_mock_api_s_allowlists_empty" {
  command = plan

  override_data {
    target = data.terraform_remote_state.apps
    values = {
      outputs = {}
    }
  }

  assert {
    condition = local.mock_api_clients == {
      app1 = ""
      app2 = ""
    }
    error_message = "An apps root state with no clients output must leave both client ids empty, not fail the plan: that is the missing output main.tf's try() absorbs."
  }
}

# --- Change request 1.5: the teardown role and the cell name ------------------------------------

run "the_teardown_principal_is_the_role_bootstrap_creates_looked_up_by_its_derived_name" {
  command = plan

  assert {
    condition     = data.aws_iam_role.teardown.name == "spa-platform-poc-teardown"
    error_message = "The root derives spa-platform-<cell>-teardown, the role infra/bootstrap creates (ADR-0014)."
  }

  assert {
    condition     = local.teardown_principal_arns == ["arn:aws:iam::111122223333:role/spa-platform-poc-teardown"]
    error_message = "With no additional principals, the only teardown principal is the ARN AWS returned for the looked-up role."
  }
}

run "an_additional_teardown_principal_is_added_and_never_replaces_the_role" {
  command = plan

  variables {
    additional_teardown_principal_arns = ["arn:aws:iam::111122223333:role/Operator"]
  }

  assert {
    condition = local.teardown_principal_arns == [
      "arn:aws:iam::111122223333:role/spa-platform-poc-teardown",
      "arn:aws:iam::111122223333:role/Operator",
    ]
    error_message = "additional_teardown_principal_arns is concatenated after the derived role, so it cannot lock out the one principal that can empty the bucket."
  }
}

run "an_additional_teardown_principal_refuses_an_assumed_role_session_arn" {
  command = plan

  variables {
    additional_teardown_principal_arns = ["arn:aws:sts::111122223333:assumed-role/Operator/someone"]
  }

  expect_failures = [var.additional_teardown_principal_arns]
}

# Change request 1.5a: every consumer but infra/bootstrap reads this output rather than deriving the
# bucket's name, so the root publishes the name at plan, and it is the one the contract examples
# render for this file's placeholders. Bootstrap applies before the bucket exists and derives the
# same default; infra/bootstrap's tests check that its teardown grant names the bucket modules/cell
# creates.
run "the_root_publishes_the_bucket_the_contract_examples_render" {
  command = plan

  assert {
    condition     = output.bucket == jsondecode(file("../../../../contracts/infra-outputs.example.json")).bucket
    error_message = "The root's bucket output is spa-platform-<cell>-<account id>-<region>-an, as contracts/infra-outputs.example.json renders it for cell poc in account 111122223333 and us-east-1."
  }
}

run "cell_refuses_pc_at_this_root" {
  command = plan

  variables {
    cell = "pc"
  }

  expect_failures = [var.cell]
}

run "cell_refuses_a_trailing_hyphen_at_this_root" {
  command = plan

  variables {
    cell = "poc-"
  }

  expect_failures = [var.cell]
}


# --- Task 1.9: the platform plan allowlist (P-01) ------------------------------------------------
#
# P-01: "Exactly one ALB, one listener, one target group, one ECS service, one bucket. No NAT
# gateway, database or EC2 instance." (docs/plan/04-tests.md). Plan row 1.9 makes it an allowlist of
# resource types and counts, and ADR-0016 makes it an assertion on the configuration, planned
# against the mock provider above, because CI holds no AWS credentials. What these runs prove is
# the plan the configuration produces, never the account.
#
# They read the configuration's source, because a plan holds no list of its resources that a test
# can enumerate: a test addresses only resources it already knows the names of, and a root test
# cannot address a module's resources at all. What only this root can hold is that modules/cell is
# the whole of its plan, and how many of each type that plan contains.
#
# Each pattern takes a block's labels in quotes, the only form `terraform fmt` leaves: CI's Format
# step and tools/check.sh both run `fmt -check` before any test, and an unquoted label fails it. A
# name is whatever sits between the quotes, since Terraform accepts capitals and hyphens in one
# (modules/cell/tests/network.tftest.hcl cites the rule). Paths are built on path.module, the root
# itself, because Terraform refuses an assertion that refers to nothing in the configuration.

run "the_root_declares_no_resource_and_calls_the_cell_module_once_and_nothing_else" {
  command = plan

  # The next run finds the module's declarations with this same pattern, which is what shows that
  # the zero found here is the root's and not the pattern's.
  assert {
    condition = length(regexall(
      "(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"",
      join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
    )) == 0
    error_message = "The platform root declares no resource of its own: its plan is modules/cell's, which the next run holds to the allowlist. A resource here, such as a NAT gateway, a rule on the task's group or a policy on its role, is in the plan with no allowlist reading it."
  }

  # A block written on one line holds one argument, and `terraform fmt -check` passes it as it is.
  # The module pattern below and the next two runs' meta-argument patterns end a block at a closing
  # brace in the first column, so they read past a one-line block into the next one, or past the
  # end. Two such blocks passed every run here: module "nat" { source = "./nat" } at the end of
  # versions.tf, which put a NAT gateway in this root's plan; and, with the allowlist raised to two
  # buckets, resource "aws_s3_bucket" "extra" { count = 2 } ahead of the platform bucket in
  # modules/cell, while the plan held three.
  assert {
    condition = alltrue([
      for header in regexall("(?m)^(?:resource|module)\\s[^\\n]*$", join("\n", concat(
        [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
        [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")],
      ))) : endswith(trimspace(header), "{")
    ])
    error_message = "Every resource and module block in the platform root and modules/cell opens a block of its own lines. A one-line block carries its source, count or for_each where the patterns that read them never look."
  }

  # Every pattern in this file finds a declaration where it starts a line. `terraform fmt -check`
  # re-indents a block and fails a comment between its keyword and its labels, but it passes a block
  # comment in front of the keyword: /* rationale */ resource "aws_s3_bucket" "unlisted_probe" {} in
  # modules/cell passed every run here, a second bucket in a plan held to one. So a line that names a
  # resource or module block names it at its start, or is a # or // comment.
  #
  # Terraform has three comment forms (https://developer.hashicorp.com/terraform/language/syntax/configuration#comments).
  # A # or // comment runs to the end of its line, so nothing after one is live; only a /* */ comment
  # can end in front of live text on its line, and inside one a line can start with #. The exemption
  # read /*<newline># rationale */ resource "aws_s3_bucket" "unlisted_probe" {} as a comment, and
  # every run passed with the second bucket. Stripping block comments before matching would mean
  # lexing strings, heredocs and nested interpolation in RE2, because these files hold /* inside
  # strings ("${aws_s3_bucket.platform.arn}/*"), and a lexer that misses one form is the next miss.
  # Every block comment ends in */ (an unterminated one does not parse), so files holding no */ hold
  # no block comment: write comments with #. A string that needs */ is built so the two characters
  # are not adjacent in the source, such as "apps/*${"/"}releases".
  assert {
    condition = !strcontains(join("\n", concat(
      [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
      [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")],
    )), "*/")
    error_message = "The platform root and modules/cell hold no /* */ comment: one can end in front of a declaration on the same line, where no pattern here reads it, and inside one a line can start with #. Write the comment with #."
  }

  assert {
    condition = alltrue([
      for line in regexall("(?m)^[^\\n]*\\b(?:resource|module)\\s+\"[^\\n]*$", join("\n", concat(
        [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
        [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")],
      ))) : can(regex("^(?:(?:resource|module)\\s|\\s*(?:#|//))", line))
    ])
    error_message = "Every resource and module block in the platform root and modules/cell starts its own line. A declaration behind a block comment, /* ... */ resource, is in the plan where no pattern here reads it."
  }

  assert {
    condition = [
      for m in regexall("(?ms)^module\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])) :
      [m[0], try(regex("(?m)^\\s*source\\s*=\\s*\"([^\"]+)\"", m[1])[0], "")]
    ] == [["cell", "../../../modules/cell"]]
    error_message = "The platform root calls one module, cell, from ../../../modules/cell, and nothing else: another module call brings resources of any type into the plan, with no allowlist naming them."
  }

  # count or for_each on the call multiplies every resource in the module, so "exactly one ALB" would
  # read true of the source and false of the plan.
  assert {
    condition = alltrue([
      for m in regexall("(?ms)^module\\s+\"[^\"]+\"\\s*\\{(.*?)^\\}", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])) :
      length(regexall("(?m)^\\s*(count|for_each)\\s*=", m[0])) == 0
    ])
    error_message = "The cell module is called with no count or for_each: either multiplies every resource in it, so one ALB becomes two."
  }

  # Terraform loads *.tf.json beside *.tf (https://developer.hashicorp.com/terraform/language/files),
  # and a block in JSON syntax is invisible to every pattern in this file.
  assert {
    condition     = length(fileset(path.module, "*.tf.json")) == 0 && length(fileset("${path.module}/../../../modules/cell", "*.tf.json")) == 0
    error_message = "The platform root and modules/cell are written in .tf files only: a .tf.json file declares blocks that no source-reading assertion sees."
  }

  assert {
    condition = length(regexall(
      "(?m)^module\\s+\"",
      join("\n", [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")])
    )) == 0
    error_message = "modules/cell calls no child module, so its own .tf files are the whole of what it adds to the plan, which is what the allowlist in the next run reads."
  }
}

# The allowlist: every type the platform plan may hold, and how many of each. The numbers come from
# the plan rows and the decisions recorded on them, not from a count of the source this reads. Row
# 1.2: a VPC, two public subnets in two zones with no NAT, so an internet gateway, a route table, its
# default route and an association per subnet; the ALB's and the task's security groups, with one
# rule into each and one out of each; and the VPC's default group, adopted with no rule (ticket 03,
# question 3). Row 1.3: one load balancer, one listener, one target group. Row 1.5: one bucket,
# private, versioned and TLS only, as its public access block, ownership controls, versioning and
# policy, with the module's default encryption beside them; and two image repositories. Row 1.4: one
# cluster, one task definition, one service and a log group, with a role for the agent and a role
# for the task, each with its one policy.
#
# A run against the module rather than the root, because only that can address the module's
# resources, and three of them are for_each: their instance counts are in the plan, not the source.
# Every other declaration carries no count or for_each, so it is exactly one instance; the first
# assertion is what makes that true rather than assumed. The run before this one holds that the
# module is the root's whole plan.
#
# Adding a type, or another of one, means editing this map in the same change, which is where the
# author is made to say what the resource is for. The run after this one holds the part of P-01 no
# edit here can change.
run "the_platform_plan_holds_exactly_the_allowlisted_types_and_counts" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]

    # modules/cell's source, read from the root's directory, which is where terraform test resolves
    # a relative path even in a run against a module.
    cell_source = join("\n", [for f in fileset("../../../modules/cell", "*.tf") : file("../../../modules/cell/${f}")])
  }

  # Only the three declarations whose instances the next assertion counts may carry count or
  # for_each. Two spaces is the canonical indent of a block's own argument, which `fmt -check` keeps,
  # and is what tells a meta-argument from a dynamic block's for_each further in.
  assert {
    condition = toset([
      for m in regexall("(?ms)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", var.cell_source) :
      "${m[0]}.${m[1]}" if length(regexall("(?m)^  (count|for_each)\\s*=", m[2])) > 0
      ]) == toset(keys({
        "aws_subnet.public"                  = aws_subnet.public
        "aws_route_table_association.public" = aws_route_table_association.public
        "aws_ecr_repository.repository"      = aws_ecr_repository.repository
    }))
    error_message = "Only the subnets, their route table associations and the image repositories may carry count or for_each: every other declaration is one instance, and that is how the next assertion counts it. A new count or for_each needs its instances counted there."
  }

  assert {
    condition = {
      for type in distinct([for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) : m[0]]) :
      type => sum([
        for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) :
        lookup({
          "aws_subnet.public"                  = length(aws_subnet.public)
          "aws_route_table_association.public" = length(aws_route_table_association.public)
          "aws_ecr_repository.repository"      = length(aws_ecr_repository.repository)
        }, "${m[0]}.${m[1]}", 1)
        if m[0] == type
      ])
      } == {
      aws_vpc                                            = 1
      aws_internet_gateway                               = 1
      aws_subnet                                         = 2
      aws_route_table                                    = 1
      aws_route                                          = 1
      aws_route_table_association                        = 2
      aws_security_group                                 = 2
      aws_vpc_security_group_ingress_rule                = 2
      aws_vpc_security_group_egress_rule                 = 2
      aws_default_security_group                         = 1
      aws_lb                                             = 1
      aws_lb_listener                                    = 1
      aws_lb_target_group                                = 1
      aws_s3_bucket                                      = 1
      aws_s3_bucket_versioning                           = 1
      aws_s3_bucket_public_access_block                  = 1
      aws_s3_bucket_ownership_controls                   = 1
      aws_s3_bucket_server_side_encryption_configuration = 1
      aws_s3_bucket_policy                               = 1
      aws_ecr_repository                                 = 2
      aws_ecs_cluster                                    = 1
      aws_ecs_task_definition                            = 1
      aws_ecs_service                                    = 1
      aws_cloudwatch_log_group                           = 1
      aws_iam_role                                       = 2
      aws_iam_role_policy                                = 2
    }
    error_message = "The platform plan holds a type the allowlist does not name, or a different number of one it does (plan row 1.9, P-01). Name the type and its count in this map, in the same change, and say what it is for."
  }
}

# P-01's own sentence, held apart from the allowlist: an edit that adds a NAT gateway to the map
# above, or a second load balancer, still fails here, because P-01 forbids them and the allowlist's
# author does not get to decide otherwise. The five types are counted from the declarations, which
# the run before this one shows are single instances unless they are the three for_each ones; none
# of the five is.
#
# The forbidden types are P-01's three words made specific. A NAT gateway. An EC2 instance, or the
# capacity that launches one: a launch template or configuration, an Auto Scaling group, a fleet or
# a spot request, a dedicated host, an ECS capacity provider (it exists only to put tasks on
# instances), an EKS node group, an EMR cluster or a Lightsail instance. A database, by every AWS
# database service's resource prefix; aws_dynamodb_table among them is a Phase 1 tripwire, because
# Session.Store admits dynamodb only in Phase 2 (Q81 in
# .scratch/overnight/questions-2026-10-15-answered.md, answered yes). The list is a floor, not a
# closure: the allowlist above is the closure, and this is what an edit to it cannot lower.
run "the_platform_plan_holds_one_alb_listener_target_group_service_and_bucket_and_no_nat_gateway_instance_or_database" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    cell_source           = join("\n", [for f in fileset("../../../modules/cell", "*.tf") : file("../../../modules/cell/${f}")])
  }

  # One declaration of each, carrying no count or for_each, is exactly one instance of each.
  assert {
    condition = alltrue([
      for type in ["aws_lb", "aws_lb_listener", "aws_lb_target_group", "aws_ecs_service", "aws_s3_bucket"] : [
        for m in regexall("(?ms)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", var.cell_source) :
        length(regexall("(?m)^  (count|for_each)\\s*=", m[2])) if m[0] == type
      ] == [0]
    ])
    error_message = "P-01: exactly one ALB, one listener, one target group, one ECS service and one bucket. Each is one declaration with no count or for_each."
  }

  assert {
    condition = [
      for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) : m[0]
      if can(regex("^aws_(nat_gateway|instance|spot_instance_request|spot_fleet_request|ec2_fleet|ec2_host|launch_template|launch_configuration|autoscaling_group|ecs_capacity_provider|eks_node_group|emr_cluster|lightsail_instance|db_.+|rds_.+|dynamodb_.+|docdb.*|neptune_.+|elasticache_.+|memorydb_.+|redshift.*|dsql_.+|keyspaces_.+|timestreamwrite_.+|opensearch_domain|elasticsearch_domain)$", m[0]))
    ] == []
    error_message = "P-01: no NAT gateway, database or EC2 instance in the platform plan. The cell sends 443 out from the task's public address (plan row 1.2), runs on Fargate (plan row 1.4) and keeps its state in S3 (contracts/s3-layout.md)."
  }
}
