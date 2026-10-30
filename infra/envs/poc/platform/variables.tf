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

  # The same rule as infra/bootstrap and modules/cell, character for character (change request
  # 1.5, ADR-0014 item 7), checked here so a bad name fails at this root's own input. 3 is the
  # frozen appId minimum the cell is published as (contracts/infra-outputs.schema.json -> cell); 22
  # keeps spa-<cell> within the load balancer's 32-character name limit (tests/root.tftest.hcl). No
  # edge hyphen (ADR-0014 item 7); a trailing one would also end spa-<cell> in modules/cell, which
  # AWS refuses in a load balancer's, target group's or repository's name (cited there).
  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$", var.cell))
    error_message = "cell must be 3 to 22 lowercase letters, digits or hyphens, and must not start or end with a hyphen."
  }
}

variable "vpc_cidr" {
  description = <<-EOT
    The cell's address range. ForwardedHeaders:TrustedNetworks in platform
    configuration names this range, so the gateway trusts the load balancer's
    X-Forwarded-For and nothing else's. The module validates the shape.
  EOT
  type        = string
  default     = "10.20.0.0/16"
}

variable "additional_teardown_principal_arns" {
  description = <<-EOT
    Principals allowed to delete objects under apps/ in addition to the teardown role, which this
    root looks up by its derived name, spa-platform-<cell>-teardown (change request 1.5, ADR-0014).
    Everyone else is denied, and that is what makes a release prefix write-once
    (contracts/s3-layout.md, test P-04).

    Additional, never instead: the list is appended to the role, so it cannot lock out the one
    principal that can empty the bucket. Each entry is an IAM user or role ARN, never the
    arn:aws:sts::...:assumed-role/... session ARN `aws sts get-caller-identity` prints, which
    aws:PrincipalArn never carries and the bucket policy's condition would therefore never match.
  EOT
  type        = list(string)
  default     = []

  validation {
    condition = alltrue([
      for arn in var.additional_teardown_principal_arns :
      can(regex("^arn:aws(-[a-z]+)*:iam::[0-9]{12}:(user|role)/[A-Za-z0-9+=,.@_/-]+$", arn))
    ])
    error_message = "additional_teardown_principal_arns takes IAM user or role ARNs. Not the account root, and not an arn:aws:sts::...:assumed-role/... session ARN."
  }
}

variable "certificate_domain" {
  description = <<-EOT
    The domain of the wildcard certificate tools/scripts/certs.sh imported, without
    the wildcard. The listener presents *.{certificate_domain}, found in ACM by that
    name and this cell's Cell tag. Run `certs.sh issue` and `certs.sh import` before
    the first apply of this root, or the lookup fails the plan.
  EOT
  type        = string
  default     = "spa-poc.test"
}

variable "health_check_path" {
  description = <<-EOT
    The load balancer's health-check path, which follows the image: / for the
    public sample image the service starts on, and /readyz once the gateway runs.
  EOT
  type        = string
  default     = "/"
}

# --- Task 1.4a -------------------------------------------------------------------------------

variable "state_bucket" {
  description = <<-EOT
    The Terraform state bucket, where the apps root's state is read from for the mock API's client
    allowlists. Leave empty for spa-platform-<cell>-tfstate-<account id>, the bootstrap's default
    name (infra/bootstrap/main.tf).
  EOT
  type        = string
  default     = ""
}

variable "gateway_image" {
  description = <<-EOT
    The gateway container's image. No default, and no default that names a tag: deploy.sh builds and
    pushes by digest and then asserts the running digest, and every other apply passes the digest
    already in state (docs/plan/05-infrastructure-and-cost.md -> What stays out of Terraform).

    The first apply of this root passes the public sample image, because the gateway image does not
    exist yet, and health_check_path follows the image.
  EOT
  type        = string
}

variable "mock_api_image" {
  description = <<-EOT
    The mock API container's image, or empty for a task with the gateway alone. Empty until task 5.3
    has built it, which is after the first apply of this root.
  EOT
  type        = string
  default     = ""
}

variable "issuers" {
  description = <<-EOT
    The issuers a registry entry may name, keyed by the name an entry uses in oauth.issuer. Each
    value comes from the identity root's outputs: `authority` is its `issuer`, and `browser_origin`
    is https:// plus its `cognito_domain` (contracts/infra-outputs.schema.json). Assumption A4 is one
    issuer per environment.

    No default: the values carry the account's own pool id, so there is nothing sensible to guess.
  EOT
  type = map(object({
    authority      = string
    profile        = string
    browser_origin = string
  }))
}

variable "allowed_host_suffixes" {
  description = <<-EOT
    The suffixes a registry host may end with. The default is the one
    contracts/platform-config.example.json shows: a host is reached through the tunnel, so it
    carries the local port (docs/plan/05-infrastructure-and-cost.md -> Workstation access).
  EOT
  type        = list(string)
  default     = [".spa-poc.test:8443"]
}

variable "allowed_upstreams" {
  description = <<-EOT
    The upstream origins a registry entry may name. The default is the pair the mock API listens on
    inside the task, which is also what fixtures/apps/*.json name as their upstreams
    (contracts/platform-config.example.json).
  EOT
  type        = list(string)
  default     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
}

# --- Continuity (#347 Q7): the inputs #338 gave the cell, passed through ---------------------------
#
# Each defaults to Phase 1's value, so a terraform.tfvars that names none of them plans the one-task
# cell G3 ran. The G6 sitting (#348) sets them in its untracked terraform.tfvars; the values are in
# terraform.tfvars.example. The module holds each value's rule and refuses dynamodb with no prefix.

variable "session_store" {
  description = <<-EOT
    Where sessions live: memory in Phase 1, dynamodb from G6 (ADR-0004, ADR-0012; #332 D6). The
    module's rule: dynamodb needs data_protection_parameter_prefix.
  EOT
  type        = string
  default     = "memory"
}

variable "data_protection_parameter_prefix" {
  description = <<-EOT
    The Parameter Store prefix of the cell's shared data-protection key ring, or empty for none, which
    is Phase 1. Set, it is exactly /spa-platform/{cell}/data-protection/ (P2-2 decision 8); the module
    refuses any other value.
  EOT
  type        = string
  default     = ""
}

variable "data_protection_kms_key_id" {
  description = <<-EOT
    The KMS key the ring's parameters are encrypted under, or empty for the account's aws/ssm key,
    which is G6's value (#332 D6: no customer-managed key).
  EOT
  type        = string
  default     = ""
}
