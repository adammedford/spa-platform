variable "cell" {
  description = <<-EOT
    The cell name. A cell is the deployment unit: ALB, service, bucket, registry,
    secrets prefix and session store (ADR-0012). It is part of every resource name,
    including the ones in this root.
  EOT
  type        = string
  default     = "poc"

  # The same rule as the platform root and modules/cell, character for character (change request
  # 1.5, ADR-0014 item 7). The lower bound of 3 is the frozen appId shape the cell is published as
  # (contracts/infra-outputs.schema.json -> cell, which references registry.schema.json -> appId).
  # The upper bound of 22 is the longest name every resource name still fits: the load balancer is
  # spa-<cell>, within its 32-character limit, and the platform root's test pins it. No edge
  # hyphen (ADR-0014 item 7); a trailing one would also end spa-<cell> in modules/cell, which AWS
  # refuses in a load balancer's, target group's or repository's name (cited there).
  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$", var.cell))
    error_message = "cell must be 3 to 22 lowercase letters, digits or hyphens, and must not start or end with a hyphen."
  }
}

variable "region" {
  description = "AWS region. The plan's default is us-east-1, and every price in the cost model is for that region."
  type        = string
  default     = "us-east-1"
}

variable "state_bucket_name" {
  description = <<-EOT
    Name of the Terraform state bucket. Leave empty to use
    spa-platform-<cell>-tfstate-<account id>, which keeps the globally unique S3
    name unique without a random suffix that would differ per checkout.
  EOT
  type        = string
  default     = ""

  validation {
    condition     = var.state_bucket_name == "" || can(regex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", var.state_bucket_name))
    error_message = "state_bucket_name must be empty or a valid S3 bucket name."
  }
}

variable "state_noncurrent_version_retention_days" {
  description = <<-EOT
    How long a superseded state version is kept. Versioning is what makes a bad
    apply recoverable, so this is the recovery window. Storage is cents per GB at
    this size; raise it rather than lower it.
  EOT
  type        = number
  default     = 90

  validation {
    condition     = var.state_noncurrent_version_retention_days >= 30
    error_message = "Keep at least 30 days of state history: it is the only way back from a bad apply."
  }
}

# --- Who may assume the CI plan role -----------------------------------------
#
# The plan's default CI is GitHub Actions, which authenticates with an OIDC token.
# Give either github_repository or additional_trusted_principal_arns; with neither,
# the apply fails rather than creating a role nobody can assume.

variable "github_repository" {
  description = "GitHub repository that runs the plans, as \"owner/repo\". Empty to trust no GitHub workflow."
  type        = string
  default     = ""

  validation {
    condition     = var.github_repository == "" || can(regex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", var.github_repository))
    error_message = "github_repository must be empty or of the form owner/repo."
  }
}

variable "github_subject_claims" {
  description = <<-EOT
    The `sub` claims allowed to assume the CI plan role, relative to the repository:
    for example "ref:refs/heads/main" or "pull_request". Each becomes
    "repo:<github_repository>:<claim>". Keep these narrow; a bare "*" would let any
    branch in the repository assume the role.
  EOT
  type        = list(string)
  default     = ["ref:refs/heads/main", "pull_request"]
}

variable "create_github_oidc_provider" {
  description = <<-EOT
    Create the GitHub Actions OIDC provider in this account. One provider per
    account is allowed, so set this false when the account already has one and pass
    its ARN in github_oidc_provider_arn.
  EOT
  type        = bool
  default     = true
}

variable "github_oidc_provider_arn" {
  description = "ARN of an existing GitHub Actions OIDC provider, used when create_github_oidc_provider is false."
  type        = string
  default     = ""
}

variable "additional_trusted_principal_arns" {
  description = <<-EOT
    Extra principals allowed to assume the CI plan role: a deployer's role, or a
    runner outside GitHub Actions. Every entry needs multi-factor or federated
    sign-in of its own; this role grants read access to the whole platform's shape.
  EOT
  type        = list(string)
  default     = []
}

variable "allow_state_lock" {
  description = <<-EOT
    Let the CI plan role write the backend's lock file. Off by default, which is why
    CI runs `terraform plan -lock=false`: a role that can write nothing cannot take
    a lock. Turn it on only if a CI plan must block a concurrent apply, and note
    that it makes the role able to create and delete objects whose key ends
    `.tflock` in the state bucket.
  EOT
  type        = bool
  default     = false
}

variable "plan_can_read_parameters" {
  description = <<-EOT
    Let the CI plan role read this cell's Parameter Store values. Terraform's
    aws_ssm_parameter resource reads a parameter's value to plan it, so without this
    the apps root's plan fails on the client-secret parameters. With it, a CI plan
    can read those client secrets and the generated test-user passwords. Both
    readings are defensible; the platform's is that a plan must be complete, so this
    defaults on and is scoped to /spa-platform/<cell>/*.
  EOT
  type        = bool
  default     = true
}

# --- The teardown role (change request 1.5, ADR-0014) ------------------------------------------

variable "teardown_trusted_principal_arns" {
  description = <<-EOT
    The humans allowed to assume the teardown role, which may delete published releases under
    apps/ in the platform bucket. Its own list, not additional_trusted_principal_arns: the CI
    role's principals read everything, these delete releases, and the two are different people.

    Explicit IAM user and role ARNs only. A user assumes the role only with MFA. A role here is an
    allowlisted federated human role (an IAM Identity Center permission set's role, say), whose MFA
    its identity provider enforces. Never the account root, never an
    arn:aws:sts::...:assumed-role/... session ARN (the form `aws sts get-caller-identity` prints),
    and never the CI plan role. No default, and the apply fails on an empty list: the humans cannot
    be derived, and a teardown role nobody can assume leaves a bucket nobody can empty.
  EOT
  type        = list(string)

  validation {
    condition = alltrue([
      for arn in var.teardown_trusted_principal_arns :
      can(regex("^arn:aws(-[a-z]+)*:iam::[0-9]{12}:(user|role)/[A-Za-z0-9+=,.@_/-]+$", arn))
    ])
    error_message = "teardown_trusted_principal_arns takes IAM user or role ARNs (arn:aws:iam::<account>:user/... or :role/...). Not the account root, and not an arn:aws:sts::...:assumed-role/... session ARN."
  }
}

variable "platform_bucket_name" {
  description = <<-EOT
    The platform bucket the teardown role may empty and the CI plan role may describe. Leave
    empty for spa-platform-<cell>-<account id>-<region>-an, the default modules/cell gives the
    bucket, derived from this root's own account and Region because this root applies before the
    bucket exists (change request 1.5a). Set it to the same value as the cell module's bucket_name
    if that is ever overridden: both grants name this one bucket's ARN, never a
    spa-platform-<cell>-* glob (ADR-0014 item 6).
  EOT
  type        = string
  default     = ""

  validation {
    condition     = var.platform_bucket_name == "" || can(regex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", var.platform_bucket_name))
    error_message = "platform_bucket_name must be empty or a valid S3 bucket name."
  }
}
