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
}

variables {
  cell                    = "poc"
  vpc_cidr                = "10.20.0.0/16"
  certificate_domain      = "spa-poc.test"
  teardown_principal_arns = ["arn:aws:iam::111122223333:role/Admin"]
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
    error_message = "The load balancer must carry the load balancer group of task 1.2 and no other."
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
    cell         = "abcdefghijklmnopqrstuv"
    gateway_port = 9090
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
    health_check_path = "/readyz"
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
    health_check_path = "readyz"
  }

  expect_failures = [var.health_check_path]
}

run "a_certificate_domain_carries_no_wildcard" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    certificate_domain = "*.spa-poc.test"
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
