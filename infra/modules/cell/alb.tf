# Task 1.3. The cell's one load balancer: internal, one HTTPS listener with the
# imported wildcard certificate, and one target group.
#
# ADR-0010 is the whole design: a wildcard certificate on one listener, forwarding to
# a single target group with the Host header preserved, and no listener rules. Host
# resolution happens in the gateway, by exact authority match, so onboarding an app
# never touches anything in this file.

locals {
  # Load balancer and target group names are capped at 32 characters, and
  # spa-platform-{cell} reaches 35 at the cell's 22-character maximum. spa-{cell} is
  # the form contracts/infra-outputs.example.json shows behind albDnsName
  # (internal-spa-poc-...), and the one the image repositories already use.
  lb_name = "spa-${var.cell}"
}

# The certificate tools/scripts/certs.sh imported. Terraform does not create it, it
# finds it, the way docs/plan/05-infrastructure-and-cost.md says: by domain name.
#
# The Cell tag is the same filter certs.sh's own lookup applies, so a certificate
# imported for another cell under the same domain is never picked up here. The leaf
# certs.sh issues is RSA 2048, which is this data source's default key type, so no
# key_types argument is needed. A lookup that finds nothing fails the plan, which is
# the right failure: the listener cannot exist without the certificate.
data "aws_acm_certificate" "wildcard" {
  domain      = "*.${var.certificate_domain}"
  types       = ["IMPORTED"]
  statuses    = ["ISSUED"]
  most_recent = true

  tags = {
    Cell = var.cell
  }
}

resource "aws_lb" "cell" {
  name               = local.lb_name
  load_balancer_type = "application"

  # Internal: reached through the Session Manager tunnel, never from the internet.
  # It sits in the two public subnets because those are the only subnets the cell
  # has; an internal load balancer gets private addresses wherever it sits.
  internal        = true
  subnets         = [for subnet in aws_subnet.public : subnet.id]
  security_groups = [aws_security_group.alb.id]

  # The gateway resolves the app from the Host header by exact authority match
  # (ADR-0010, N-08). Preserving it keeps the port the browser sent, which is what
  # the registry's host carries (app1.spa-poc.test:8443 through the tunnel).
  preserve_host_header = true

  # A header whose name is not a valid HTTP field name is removed before it reaches
  # the gateway, rather than left for the gateway to interpret.
  drop_invalid_header_fields = true

  # The load balancer appends the client address to X-Forwarded-For, which is what
  # the gateway's forward limit of one reads (T6, N-09). This is the provider's
  # default, written down because the gateway depends on it.
  xff_header_processing_mode = "append"

  # Teardown destroys this root and then proves the account is empty by tag query
  # (P-05). Deletion protection would make the first step fail.
  enable_deletion_protection = false

  tags = { Name = local.lb_name }
}

resource "aws_lb_target_group" "gateway" {
  name = local.lb_name

  # A Fargate task in awsvpc mode registers by address, not by instance.
  target_type = "ip"
  protocol    = "HTTP"
  port        = var.gateway_port
  vpc_id      = aws_vpc.cell.id

  # The path follows the image: / for the sample image the service starts on, and
  # /readyz for the gateway, which answers 200 only once a registry snapshot is live
  # (contracts/pipeline.md). The gateway answers it on any host, so the health
  # checker's Host, the target's own address, needs no registry entry.
  health_check {
    path     = var.health_check_path
    protocol = "HTTP"
    port     = "traffic-port"
    matcher  = "200"
  }

  tags = { Name = local.lb_name }
}

# The one listener. HTTPS on 443 and nothing else: there is no port 80 listener and
# no redirect, because nothing reaches this load balancer except the tunnel, which
# forwards to 443.
resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.cell.arn
  port              = 443
  protocol          = "HTTPS"
  certificate_arn   = data.aws_acm_certificate.wildcard.arn

  # TLS 1.3 and 1.2 only.
  ssl_policy = "ELBSecurityPolicy-TLS13-1-2-2021-06"

  # No listener rules, ever (ADR-0010): every host goes to the one target group, and
  # the gateway answers 404 to a host the registry does not know.
  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.gateway.arn
  }

  tags = { Name = "${local.lb_name}-https" }
}
