# Task 1.2. The cell's network: a VPC, two public subnets in two zones, and the two
# security groups that are the whole of this platform's network boundary.
#
# What is deliberately absent is as much of the design as what is here. There is no
# NAT gateway, no bastion and no database: the task sends 443 out from a public
# address, and test P-01 reads the plan and fails on any of the three.

data "aws_availability_zones" "available" {
  state = "available"
}

locals {
  # Two zones, in the order the account reports them, so the pair is stable across
  # applies. Two is what the load balancer requires and one more than the platform
  # needs, since there is one task.
  zones = slice(data.aws_availability_zones.available.names, 0, 2)

  # How many bits to add to the VPC's prefix to reach a /24. The variable's
  # validation keeps this at 1 or more.
  subnet_newbits = 24 - tonumber(split("/", var.vpc_cidr)[1])

  name_prefix = "spa-platform-${var.cell}"
}

resource "aws_vpc" "cell" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = { Name = local.name_prefix }
}

# The public address the task sends 443 out from. This is what stands in for a NAT
# gateway, and it is the saving: a NAT gateway is about $33 a month, more than
# everything else in this cell together.
resource "aws_internet_gateway" "cell" {
  vpc_id = aws_vpc.cell.id

  tags = { Name = local.name_prefix }
}

resource "aws_subnet" "public" {
  for_each = { for index, zone in local.zones : zone => index }

  vpc_id            = aws_vpc.cell.id
  availability_zone = each.key
  cidr_block        = cidrsubnet(var.vpc_cidr, local.subnet_newbits, each.value)

  # The task needs a public address to reach Cognito, S3, Parameter Store, ECR and
  # logs with no NAT gateway. The load balancer is internal even so: it is reached
  # through the Session Manager tunnel, never from the internet.
  map_public_ip_on_launch = true

  tags = { Name = "${local.name_prefix}-public-${each.key}" }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.cell.id

  tags = { Name = "${local.name_prefix}-public" }
}

resource "aws_route" "internet" {
  route_table_id         = aws_route_table.public.id
  destination_cidr_block = "0.0.0.0/0"
  gateway_id             = aws_internet_gateway.cell.id
}

resource "aws_route_table_association" "public" {
  for_each = aws_subnet.public

  subnet_id      = each.value.id
  route_table_id = aws_route_table.public.id
}

# --- The two security groups ------------------------------------------------------
#
# They refer to each other, so every rule is its own resource rather than an inline
# block: an inline ingress on one group and an inline egress on the other would be a
# cycle Terraform cannot plan.

resource "aws_security_group" "alb" {
  name        = "${local.name_prefix}-alb"
  description = "The internal load balancer. Accepts 443 from the task group only."
  vpc_id      = aws_vpc.cell.id

  tags = { Name = "${local.name_prefix}-alb" }
}

resource "aws_security_group" "task" {
  name        = "${local.name_prefix}-task"
  description = "The gateway task. Accepts ${var.gateway_port} from the load balancer only."
  vpc_id      = aws_vpc.cell.id

  tags = { Name = "${local.name_prefix}-task" }
}

# The tunnel's path, and the only way in. A port forward through the task reaches
# the load balancer from inside the task group, which is why 443 is open to that
# group and to nothing else. There is no ingress rule from the internet anywhere in
# this file.
resource "aws_vpc_security_group_ingress_rule" "alb_https_from_task" {
  security_group_id            = aws_security_group.alb.id
  description                  = "HTTPS from the task group, which is the Session Manager tunnel's path"
  referenced_security_group_id = aws_security_group.task.id
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
}

resource "aws_vpc_security_group_egress_rule" "alb_to_task" {
  security_group_id            = aws_security_group.alb.id
  description                  = "To the gateway's port on the task group"
  referenced_security_group_id = aws_security_group.task.id
  ip_protocol                  = "tcp"
  from_port                    = var.gateway_port
  to_port                      = var.gateway_port
}

resource "aws_vpc_security_group_ingress_rule" "task_from_alb" {
  security_group_id            = aws_security_group.task.id
  description                  = "The gateway's port, from the load balancer only (test P-06)"
  referenced_security_group_id = aws_security_group.alb.id
  ip_protocol                  = "tcp"
  from_port                    = var.gateway_port
  to_port                      = var.gateway_port
}

# Out to Cognito, S3, Parameter Store, ECR and CloudWatch Logs. HTTPS only: every
# one of them is an HTTPS endpoint, so a wider egress rule would buy nothing.
resource "aws_vpc_security_group_egress_rule" "task_https_out" {
  security_group_id = aws_security_group.task.id
  description       = "HTTPS out, for Cognito, S3, Parameter Store, ECR and logs"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
}
