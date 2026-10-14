variable "cell" {
  description = "The cell name (ADR-0012). The same value as Platform:Cell and as cell in the infrastructure outputs."
  type        = string
  default     = "poc"
}

variable "region" {
  description = "AWS region. Every price in the cost model is for us-east-1."
  type        = string
  default     = "us-east-1"
}

variable "domain_prefix" {
  description = <<-EOT
    The managed-login prefix domain. Leave empty for spa-platform-<cell>-<account id>: prefixes are
    unique per region across every AWS account, and the account id keeps this one unique without
    a random suffix that would differ per checkout, as the state bucket's name does.
  EOT
  type        = string
  default     = ""
}
