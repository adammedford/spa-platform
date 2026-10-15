variable "cell" {
  description = <<-EOT
    The cell name. A cell is the deployment unit: ALB, service, bucket, registry,
    secrets prefix and session store (ADR-0012). It is part of every resource name,
    and a Phase 2 cell is one more instance of this module.
  EOT
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{0,20}[a-z0-9]$", var.cell))
    error_message = "The cell name is 2 to 22 lowercase letters, digits or hyphens, and does not start or end with a hyphen. It goes into bucket and repository names."
  }
}

variable "vpc_cidr" {
  description = <<-EOT
    The cell's address range. Two /24 subnets are carved out of it, one per zone.
    Platform configuration's ForwardedHeaders:TrustedNetworks names this range, so
    the gateway trusts X-Forwarded-For from the load balancer and from nowhere else.

    No default: the root decides the range, and this module is called by each cell's
    root. The poc root's default is 10.20.0.0/16.
  EOT
  type        = string

  validation {
    # The whole condition is inside can(), because a value with no "/" would make
    # split(...)[1] raise an evaluation error instead of showing error_message.
    #
    # /23 is the floor, not /24: two /24 subnets are carved out, and at /24 there are
    # no bits left to carve with, so cidrsubnet would fail at apply rather than here.
    condition     = can(cidrnetmask(var.vpc_cidr)) && can(tonumber(split("/", var.vpc_cidr)[1]) <= 23) && tonumber(split("/", var.vpc_cidr)[1]) <= 23
    error_message = "vpc_cidr is an IPv4 CIDR block of /23 or larger, because two /24 subnets are carved out of it."
  }
}

variable "gateway_port" {
  description = <<-EOT
    The port the gateway container listens on. The load balancer sends to it and
    nothing else reaches it: test P-06 shows a connection from outside to the task's
    public address on this port timing out.
  EOT
  type        = number
  default     = 8080
}

variable "bucket_name" {
  description = <<-EOT
    The platform bucket's name. Empty means spa-platform-{cell}, which is what
    contracts/infra-outputs.example.json and Registry:Bucket in platform
    configuration both show. This is the platform's one private bucket; the
    Terraform state bucket is a different one, created by infra/bootstrap.
  EOT
  type        = string
  default     = ""
}

variable "teardown_principal_arns" {
  description = <<-EOT
    The principals allowed to delete objects under apps/. Everyone else is denied,
    which is what makes a release prefix write-once: without the delete rule a
    delete followed by a write would replace a release (contracts/s3-layout.md).

    It cannot be empty. A bucket nobody may empty is a bucket teardown.sh cannot
    destroy, and P-05 would then fail forever. Name the role or user that runs
    teardown.sh. Change request 1.5 asks the integrator where that ARN should come
    from, because no root creates a teardown role today.
  EOT
  type        = list(string)

  validation {
    condition     = length(var.teardown_principal_arns) > 0
    error_message = "Name at least one principal that may delete under apps/, or nothing can ever empty the bucket and teardown cannot destroy it."
  }
}

variable "certificate_domain" {
  description = <<-EOT
    The domain the cell's wildcard certificate covers, without the wildcard: the
    load balancer's listener presents *.{certificate_domain}. tools/scripts/certs.sh
    imports that certificate into ACM, tagged with this cell, and the module finds it
    there; Terraform never creates it.

    No default: the domain belongs to the cell, so the root decides it. The poc
    root's default is spa-poc.test, which is certs.sh's default too.
  EOT
  type        = string

  validation {
    # The base certificate domain uses the same DNS grammar as albDnsName. Read
    # the repository contract rather than maintaining a second copy of its pattern.
    condition     = can(regex(jsondecode(file("${path.module}/../../../contracts/infra-outputs.schema.json"))["$defs"].dnsName.pattern, var.certificate_domain))
    error_message = "certificate_domain is a dotted lowercase name with no wildcard and no trailing dot, such as spa-poc.test."
  }
}

variable "health_check_path" {
  description = <<-EOT
    The path the load balancer's health check requests. It follows the image the
    service runs: / for the public sample image the service starts on, and /readyz
    for the gateway, which answers 200 only once a registry snapshot is live
    (contracts/pipeline.md).
  EOT
  type        = string
  default     = "/"

  validation {
    condition     = can(regex("^/[A-Za-z0-9._~/-]*$", var.health_check_path))
    error_message = "health_check_path is an absolute path with no query string, such as / or /readyz."
  }
}
