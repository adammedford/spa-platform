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

# Bootstrap grants the teardown role spa-platform-<cell> by default; this is the bucket that name has
# to be.
run "the_bucket_is_the_one_bootstrap_grants_the_teardown_role" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
  }

  assert {
    condition     = aws_s3_bucket.platform.bucket == "spa-platform-poc"
    error_message = "The platform bucket must be spa-platform-<cell>, which infra/bootstrap's teardown policy names by default. Change both together."
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
