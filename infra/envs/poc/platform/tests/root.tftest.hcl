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
      arn      = "arn:aws:elasticloadbalancing:us-east-1:111122223333:loadbalancer/app/spa-poc/0123456789abcdef"
      dns_name = "internal-spa-poc-123456789.us-east-1.elb.amazonaws.com"
    }
  }

  mock_resource "aws_lb_target_group" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:targetgroup/spa-poc/0123456789abcdef"
    }
  }
}

variables {
  teardown_principal_arns = ["arn:aws:iam::111122223333:role/Admin"]
}

run "the_root_publishes_the_load_balancer_dns_name" {
  command = apply

  assert {
    condition     = module.cell.alb_dns_name == output.alb_dns_name
    error_message = "The root must publish the module's load balancer DNS name as alb_dns_name, the contract's albDnsName."
  }

  assert {
    condition     = can(regex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", output.alb_dns_name))
    error_message = "alb_dns_name must have the shape of contracts/infra-outputs.schema.json's dnsName."
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
    cell               = "poc"
    vpc_cidr           = "10.20.0.0/16"
    certificate_domain = "spa-poc.test"
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
    condition     = length(aws_lb.cell.name) <= 32 && aws_lb.cell.name == "spa-poc"
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
    condition     = data.aws_acm_certificate.wildcard.domain == "*.spa-poc.test" && data.aws_acm_certificate.wildcard.tags["Cell"] == "poc"
    error_message = "The certificate is found by its wildcard domain and this cell's Cell tag, the filter certs.sh uses."
  }

  assert {
    condition     = data.aws_acm_certificate.wildcard.types == tolist(["IMPORTED"])
    error_message = "Only an imported certificate is looked up: certs.sh imports it."
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
      && aws_lb_target_group.gateway.port == 8080
    )
    error_message = "The target group sends HTTP to the gateway's port on a Fargate task's address."
  }

  assert {
    condition     = aws_lb_target_group.gateway.health_check[0].path == "/"
    error_message = "The health-check path defaults to /, for the sample image."
  }
}

run "the_health_check_path_follows_the_variable" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    cell               = "poc"
    vpc_cidr           = "10.20.0.0/16"
    certificate_domain = "spa-poc.test"
    health_check_path  = "/readyz"
  }

  assert {
    condition     = aws_lb_target_group.gateway.health_check[0].path == "/readyz"
    error_message = "The health-check path is the variable: /readyz for the gateway."
  }
}

run "a_health_check_path_must_be_absolute" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    cell               = "poc"
    vpc_cidr           = "10.20.0.0/16"
    certificate_domain = "spa-poc.test"
    health_check_path  = "readyz"
  }

  expect_failures = [var.health_check_path]
}

run "a_certificate_domain_carries_no_wildcard" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    cell               = "poc"
    vpc_cidr           = "10.20.0.0/16"
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
