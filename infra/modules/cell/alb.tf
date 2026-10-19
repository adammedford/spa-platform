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
# The Cell tag prevents a lookup from selecting another cell's certificate. Tags
# are exact-match filters, not merely returned metadata:
# https://registry.terraform.io/providers/hashicorp/aws/6.68.0/docs/data-sources/acm_certificate
# Key types mirror tools/scripts/certs.sh's lookup, including a future non-RSA leaf.
# A lookup that finds nothing fails the plan: the listener needs the certificate.
data "aws_acm_certificate" "wildcard" {
  domain      = "*.${var.certificate_domain}"
  types       = ["IMPORTED"]
  statuses    = ["ISSUED"]
  most_recent = true
  key_types   = ["RSA_2048", "RSA_3072", "RSA_4096", "EC_prime256v1", "EC_secp384r1"]

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

  # Task 1.3 requires invalid header names to be dropped before the gateway.
  # AWS documents routing.http.drop_invalid_header_fields.enabled here:
  # https://docs.aws.amazon.com/cli/latest/reference/elbv2/modify-load-balancer-attributes.html
  drop_invalid_header_fields = true

  # The load balancer appends the client address to X-Forwarded-For, which is what
  # the gateway's forward limit of one reads (T6, N-09). This is the provider's
  # default, written down because the gateway depends on it.
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/application/x-forwarded-headers.html
  xff_header_processing_mode = "append"

  # Teardown destroys this root and then proves the account is empty by tag query
  # (P-05). Deletion protection would make the first step fail.
  enable_deletion_protection = false

  # No access_logs block: access logs stay off in Phase 1 (Q8 on #98), and off is
  # AWS's default. An entry records the URL as the client sent it, and the login
  # callback's URL carries the authorization code and the state value in its query
  # string (T13). ADR-0007's logging rules say a query string is never logged, for
  # exactly that reason. T13 and ADR-0007 accept the code appearing "in URLs and
  # ALB access logs" in Phase 1; with no access_logs block, only the URL half is
  # live (Q93 in .scratch/overnight/questions-2026-10-15-answered.md, answered yes).
  # Delivered to S3, the logs would also need a bucket of their own: the cell's one
  # bucket holds the registry and the releases (contracts/s3-layout.md), and P-01
  # allows no second. Phase 1 runs one log stream and no ALB access logs
  # (docs/plan/06-phase-2-and-risks.md -> Enterprise gaps).
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-access-logs.html

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

  # Implementation choice: pin TLS 1.3 and 1.2 rather than the older API default.
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/application/describe-ssl-policies.html
  ssl_policy = "ELBSecurityPolicy-TLS13-1-2-2021-06"

  # No listener rules, ever (ADR-0010): every host goes to the one target group, and
  # the gateway answers 404 to a host the registry does not know.
  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.gateway.arn
  }

  tags = { Name = "${local.lb_name}-https" }
}
