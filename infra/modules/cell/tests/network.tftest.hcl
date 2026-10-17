# Task 1.2. The cell's network boundary, checked against a mock AWS provider: no credentials, no
# AWS apply and no network beyond `terraform init`.
#   terraform -chdir=infra/modules/cell init -backend=false
#   terraform -chdir=infra/modules/cell test
#
# Plan row 1.2's done-when is "the plan has no NAT, bastion or database, and no other ingress rule".
# Under ADR-0016 both halves are properties of the configuration, proved here at plan time. The
# resource-type allowlist in module.tftest.hcl holds the first half: no NAT gateway, instance or
# database type can be declared. This file holds the second. The cell declares exactly the four
# security-group rules plan row 1.2 names, two in each direction, and the VPC's default group,
# which AWS creates admitting all traffic from its own members, is adopted with none. Two runs check
# what a deployment would otherwise trip over first: where the two subnets go, and that every
# description is one EC2 accepts.
#
# What it does not prove is the deployed half of P-06, that a connection from outside to the task's
# public address on the gateway's port times out. That needs the applied cell and runs at G3 (task
# 6.9). Rules declared outside this module, in the platform root, are row 1.9's root allowlist.
#
# Its own file, rather than more runs in module.tftest.hcl, so the plan-time ids below are this
# file's alone: an override applies to every run in its file, and that file's mock apply run sets
# its own.

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

  # The provider validates a policy document's JSON at plan time, so the generated string a mock
  # would otherwise return for `json` fails the plan before any assertion runs.
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }
}

# The VPC's id and the two groups' ids, known at plan time so a rule can be checked to sit on one
# group and name the other, and the default group to belong to this VPC. Without them both sides of
# every comparison are unknown until an apply.
override_resource {
  target          = aws_vpc.cell
  override_during = plan
  values          = { id = "vpc-cell" }
}

override_resource {
  target          = aws_security_group.alb
  override_during = plan
  values          = { id = "sg-alb" }
}

override_resource {
  target          = aws_security_group.task
  override_during = plan
  values          = { id = "sg-task" }
}

variables {
  cell                    = "poc"
  vpc_cidr                = "10.20.0.0/16"
  certificate_domain      = "spa-poc.test"
  teardown_principal_arns = ["arn:aws:iam::111122223333:role/Admin"]
  gateway_image           = "public.ecr.aws/nginx/nginx@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

  issuers = {
    "cognito-poc" = {
      authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
      profile        = "cognito"
      browser_origin = "https://spa-poc.auth.us-east-1.amazoncognito.com"
    }
  }

  allowed_host_suffixes = [".spa-poc.test:8443"]
  allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
}

# --- Exactly four rules ------------------------------------------------------------------------

# Read from the module's source, because a plan holds no list of the resources of a type: a test can
# only address the ones it already knows the names of, so a fifth rule would pass every assertion
# that names the four. This is the technique module.tftest.hcl's resource-type allowlist uses, and
# that allowlist is what keeps a rule from arriving as aws_security_group_rule instead.
run "the_cell_declares_exactly_the_four_security_group_rules_plan_row_1_2_names" {
  command = plan

  assert {
    condition = toset([
      for m in regexall(
        "(?m)^resource \"aws_vpc_security_group_ingress_rule\" \"([a-z0-9_]+)\"",
        join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
      ) : m[0]
    ]) == toset(["alb_https_from_task", "task_from_alb"])
    error_message = "The cell admits two things and no third (plan row 1.2): 443 to the ALB security group from the task security group, and the gateway's port to the task security group from the ALB security group."
  }

  assert {
    condition = toset([
      for m in regexall(
        "(?m)^resource \"aws_vpc_security_group_egress_rule\" \"([a-z0-9_]+)\"",
        join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
      ) : m[0]
    ]) == toset(["alb_to_task", "task_https_out"])
    error_message = "The cell sends two things and no third (plan row 1.2): the ALB security group to the gateway's port on the task security group, and the task security group's 443 out."
  }

  assert {
    condition = toset([
      for m in regexall(
        "(?m)^resource \"aws_security_group\" \"([a-z0-9_]+)\"",
        join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
      ) : m[0]
    ]) == toset(["alb", "task"])
    error_message = "The cell has two security groups, the ALB's and the task's, and together they are its whole network boundary (docs/plan/01-plan.md -> Trust boundaries). A third group is a third boundary nothing here checks."
  }

  # An inline rule is a rule too, and the four assertions on values below never see one: ingress and
  # egress are optional and computed on aws_security_group, so unset they are unknown at plan time.
  # The provider also warns against mixing inline rules with rule resources, which overwrite each
  # other: https://registry.terraform.io/providers/hashicorp/aws/latest/docs/resources/security_group
  assert {
    condition = alltrue([
      for block in regexall(
        "(?ms)^resource \"aws_security_group\" \"[a-z0-9_]+\" \\{.*?^\\}",
        join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
      ) : length(regexall("(?m)^\\s*(dynamic\\s+\")?(ingress|egress)\"?\\s*[={]", block)) == 0
    ])
    error_message = "Neither security group declares an inline ingress or egress rule, in block, argument or dynamic form: every rule is its own resource, which is what the assertions on the four rules read."
  }
}

# --- What each rule admits ---------------------------------------------------------------------

# Every rule's port, protocol, group and peer, at plan time. The ports are plan row 1.2's own
# numbers where the row fixes one, 443, and otherwise the port the load balancer actually sends to,
# read off the target group. The run changes the gateway's port, because at the default 8080 a rule
# that wrote the number in would pass alongside one that follows var.gateway_port.
run "each_rule_admits_one_port_between_the_two_groups_and_only_the_task_s_443_leaves_the_vpc" {
  command = plan

  variables {
    gateway_port = 9090
  }

  # The tunnel's path, and the only way in (docs/plan/05-infrastructure-and-cost.md -> Workstation
  # access): the Session Manager port forward runs through the task to the listener on 443.
  assert {
    condition = (
      aws_vpc_security_group_ingress_rule.alb_https_from_task.security_group_id == aws_security_group.alb.id
      && aws_vpc_security_group_ingress_rule.alb_https_from_task.referenced_security_group_id == aws_security_group.task.id
      && aws_vpc_security_group_ingress_rule.alb_https_from_task.ip_protocol == "tcp"
      && aws_vpc_security_group_ingress_rule.alb_https_from_task.from_port == 443
      && aws_vpc_security_group_ingress_rule.alb_https_from_task.to_port == 443
    )
    error_message = "The ALB security group admits TCP 443 from the task security group and from nothing else (plan row 1.2): that is the tunnel's path to the listener."
  }

  # P-06, plan half: "The task group admits 8080 from the ALB group only" (docs/plan/04-tests.md).
  assert {
    condition = (
      aws_vpc_security_group_ingress_rule.task_from_alb.security_group_id == aws_security_group.task.id
      && aws_vpc_security_group_ingress_rule.task_from_alb.referenced_security_group_id == aws_security_group.alb.id
      && aws_vpc_security_group_ingress_rule.task_from_alb.ip_protocol == "tcp"
      && aws_vpc_security_group_ingress_rule.task_from_alb.from_port == aws_lb_target_group.gateway.port
      && aws_vpc_security_group_ingress_rule.task_from_alb.to_port == aws_lb_target_group.gateway.port
    )
    error_message = "The task security group admits the gateway's port, the one the target group sends to, from the ALB security group alone (P-06): only the ALB reaches the gateway (docs/plan/01-plan.md -> Invariants)."
  }

  assert {
    condition = (
      aws_vpc_security_group_egress_rule.alb_to_task.security_group_id == aws_security_group.alb.id
      && aws_vpc_security_group_egress_rule.alb_to_task.referenced_security_group_id == aws_security_group.task.id
      && aws_vpc_security_group_egress_rule.alb_to_task.ip_protocol == "tcp"
      && aws_vpc_security_group_egress_rule.alb_to_task.from_port == aws_lb_target_group.gateway.port
      && aws_vpc_security_group_egress_rule.alb_to_task.to_port == aws_lb_target_group.gateway.port
    )
    error_message = "The ALB security group sends to the gateway's port on the task security group and nowhere else (plan row 1.2)."
  }

  # Out to Cognito, S3, Parameter Store, ECR and logs from the task's public address: there is no
  # NAT gateway (plan row 1.2, docs/plan/05-infrastructure-and-cost.md -> Phase 1 topology).
  assert {
    condition = (
      aws_vpc_security_group_egress_rule.task_https_out.security_group_id == aws_security_group.task.id
      && aws_vpc_security_group_egress_rule.task_https_out.cidr_ipv4 == "0.0.0.0/0"
      && aws_vpc_security_group_egress_rule.task_https_out.ip_protocol == "tcp"
      && aws_vpc_security_group_egress_rule.task_https_out.from_port == 443
      && aws_vpc_security_group_egress_rule.task_https_out.to_port == 443
    )
    error_message = "The task security group sends TCP 443 out and nothing else: every service the task calls is an HTTPS endpoint (plan row 1.2)."
  }

  # The anti-widening half. A rule that names an address range is a rule anyone in that range
  # meets, so only the task's 443 out may carry one, and the three between the two groups name the
  # other group and nothing else.
  assert {
    condition = [
      for name, rule in {
        alb_https_from_task = aws_vpc_security_group_ingress_rule.alb_https_from_task
        task_from_alb       = aws_vpc_security_group_ingress_rule.task_from_alb
        alb_to_task         = aws_vpc_security_group_egress_rule.alb_to_task
        task_https_out      = aws_vpc_security_group_egress_rule.task_https_out
      } : name if rule.cidr_ipv4 != null || rule.cidr_ipv6 != null || rule.prefix_list_id != null
    ] == ["task_https_out"]
    error_message = "Only the task's 443 out names an address range. A CIDR or a prefix list on any other rule admits whoever holds an address in it, which is the ingress plan row 1.2 forbids."
  }
}

# --- Where the two subnets go ------------------------------------------------------------------

# The query, because the filter runs in AWS and a mock returns whatever names it is given: what the
# module controls, and so what a plan can check, is the question it asks. The provider returns the
# zones sorted by name, and a Local Zone's name sorts before every Availability Zone in its Region
# (us-east-1-bos-1a before us-east-1a), so a Local Zone the account opts into would otherwise
# become one of the two subnets.
run "the_two_public_subnets_sit_in_two_zones_that_need_no_opt_in" {
  command = plan

  # "opt-in-not-required" is the value every Availability Zone carries, and Local and Wavelength
  # Zones never do: https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_AvailabilityZone.html
  assert {
    condition = anytrue([
      for f in data.aws_availability_zones.available.filter :
      f.name == "opt-in-status" && toset(f.values) == toset(["opt-in-not-required"])
    ])
    error_message = "The zone query asks for zones that need no opt-in, which are Availability Zones only: a Local Zone the account opts into must not become one of the cell's subnets."
  }

  assert {
    condition     = data.aws_availability_zones.available.state == "available"
    error_message = "The zone query asks for zones in the available state, so an impaired zone is not chosen."
  }

  # Two subnets in two zones (plan row 1.2), each a zone the query returned.
  assert {
    condition = (
      length(aws_subnet.public) == 2
      && length(toset([for s in aws_subnet.public : s.availability_zone])) == 2
      && alltrue([for s in aws_subnet.public : contains(data.aws_availability_zones.available.names, s.availability_zone)])
    )
    error_message = "The cell has two public subnets in two different zones, both from the zone query: the load balancer needs two zones (plan row 1.2)."
  }
}

# --- The group AWS creates ---------------------------------------------------------------------

# Every VPC comes with a default security group that admits all traffic from its own members and
# sends anything anywhere, and AWS puts any resource created without a group of its own into it:
# https://docs.aws.amazon.com/vpc/latest/userguide/default-security-group.html
# So "no other ingress rule" is true of the account only once that group is empty as well.
run "the_vpc_s_default_security_group_is_adopted_and_holds_no_rule" {
  command = plan

  # Exactly one, on this VPC: a second declaration of the same group would be a way to give it a
  # rule that the assertion below never reads.
  assert {
    condition = toset([
      for m in regexall(
        "(?m)^resource \"aws_default_security_group\" \"([a-z0-9_]+)\"",
        join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
      ) : m[0]
    ]) == toset(["cell"])
    error_message = "The cell adopts its VPC's default security group once, as aws_default_security_group.cell."
  }

  assert {
    condition     = aws_default_security_group.cell.vpc_id == aws_vpc.cell.id
    error_message = "The adopted default security group is the cell VPC's own."
  }

  # Known at plan time only because the module sets both to an empty list. Left unset they are
  # unknown here, and in AWS a rule added by hand later would never show in a plan: unset, Terraform
  # retains and ignores what exists (https://developer.hashicorp.com/terraform/language/attr-as-blocks).
  assert {
    condition = (
      length(aws_default_security_group.cell.ingress) == 0
      && length(aws_default_security_group.cell.egress) == 0
    )
    error_message = "The default security group holds no ingress and no egress rule. AWS creates it admitting all traffic from its members (plan row 1.2: no other ingress rule)."
  }
}

# --- What EC2 accepts --------------------------------------------------------------------------

# EC2 refuses a description outside a fixed character set, and the refusal comes at apply, which no
# plan, mocked or real, reaches. An apostrophe is outside every one of these sets, and three of the
# four rules first carried one ("the tunnel's path"). All four rules are held to the narrowest set,
# a rule that names a group, which has no "&":
# https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_UserIdGroupPair.html
# A rule that names a range also takes "&": https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_IpRange.html
# A group: https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_CreateSecurityGroup.html
run "every_group_and_rule_description_is_one_ec2_accepts" {
  command = plan

  assert {
    condition = alltrue([
      for description in [
        aws_vpc_security_group_ingress_rule.alb_https_from_task.description,
        aws_vpc_security_group_ingress_rule.task_from_alb.description,
        aws_vpc_security_group_egress_rule.alb_to_task.description,
        aws_vpc_security_group_egress_rule.task_https_out.description,
      ] : can(regex("^[A-Za-z0-9 ._:/()#,@\\[\\]+=;{}!$*-]{0,255}$", description))
    ])
    error_message = "A rule's description uses only a-z, A-Z, 0-9, spaces and ._-:/()#,@[]+=;{}!$* and is at most 255 characters, or EC2 refuses the rule at apply. No apostrophe."
  }

  assert {
    condition = alltrue([
      for description in [aws_security_group.alb.description, aws_security_group.task.description] :
      can(regex("^[A-Za-z0-9 ._:/()#,@\\[\\]+=&;{}!$*-]{0,255}$", description))
    ])
    error_message = "A security group's description uses only a-z, A-Z, 0-9, spaces and ._-:/()#,@[]+=&;{}!$* and is at most 255 characters, or EC2 refuses the group at apply."
  }
}
