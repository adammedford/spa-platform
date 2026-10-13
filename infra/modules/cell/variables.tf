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
  EOT
  type        = string
  default     = "10.20.0.0/16"

  validation {
    condition     = can(cidrnetmask(var.vpc_cidr)) && tonumber(split("/", var.vpc_cidr)[1]) <= 24
    error_message = "vpc_cidr is an IPv4 CIDR block of /24 or larger, because two /24 subnets are carved out of it."
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
