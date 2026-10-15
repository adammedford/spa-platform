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
    X-Forwarded-For and nothing else's. The module validates the shape.
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
