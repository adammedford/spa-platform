variable "region" {
  description = "The region this cell lives in. The same region as the state bucket."
  type        = string
  default     = "us-east-1"
}

variable "cell" {
  description = <<-EOT
    The cell name, which is part of every resource name (ADR-0012). It is the same
    value as Platform:Cell in platform configuration and `cell` in the
    infrastructure outputs.
  EOT
  type        = string
  default     = "poc"
}

variable "vpc_cidr" {
  description = <<-EOT
    The cell's address range. ForwardedHeaders:TrustedNetworks in platform
    configuration names this range, so the gateway trusts the load balancer's
    X-Forwarded-For and nothing else's.
  EOT
  type        = string
  default     = "10.20.0.0/16"
}

variable "teardown_principal_arns" {
  description = <<-EOT
    The principals allowed to delete objects under apps/, which is what lets
    teardown.sh empty the bucket. Everyone else is denied, and that is what makes a
    release prefix write-once (contracts/s3-layout.md, test P-04).

    There is no default. No root creates a teardown role today, so this is the ARN
    of whoever runs teardown.sh; change request 1.5 asks the integrator where it
    should come from.
  EOT
  type        = list(string)
}
