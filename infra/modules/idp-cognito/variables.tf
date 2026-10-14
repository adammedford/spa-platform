variable "cell" {
  description = "The cell name. Part of every resource name (ADR-0012) and of every parameter path."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,19}$", var.cell))
    error_message = "cell must be 2 to 20 characters of lowercase letters, digits or hyphens, starting with a letter."
  }
}

variable "domain_prefix" {
  description = <<-EOT
    The managed-login prefix domain: <prefix>.auth.<region>.amazoncognito.com. Prefixes are
    unique per region across every AWS account, and Cognito refuses one containing "aws",
    "amazon" or "cognito".
  EOT
  type        = string

  validation {
    condition = (
      can(regex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", var.domain_prefix))
      && !can(regex("aws|amazon|cognito", var.domain_prefix))
    )
    error_message = "domain_prefix must be 1 to 63 lowercase letters, digits or inner hyphens, and must not contain aws, amazon or cognito."
  }
}

variable "resource_servers" {
  description = "The resource servers and their scopes, in the shape of fixtures/cognito/pool.json -> resourceServers."
  type = list(object({
    identifier = string
    name       = string
    scopes = list(object({
      name        = string
      description = string
    }))
  }))
}

variable "users" {
  description = <<-EOT
    The test users, in the shape of fixtures/cognito/pool.json -> users. No password is passed
    in: this module generates each one into Parameter Store.
  EOT
  type = list(object({
    username = string
    email    = string
    name     = string
  }))

  validation {
    # The same lexical form contracts/infra-outputs.schema.json gives testUsers' keys, so every
    # user name here is a valid key there and a valid parameter path segment.
    condition     = alltrue([for u in var.users : can(regex("^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$", u.username)) && length(u.username) <= 128])
    error_message = "Every username must match ^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$ (contracts/infra-outputs.schema.json -> testUsers)."
  }
}

variable "password_length" {
  description = "Length of each generated test-user password."
  type        = number
  default     = 32

  validation {
    condition     = var.password_length >= 16 && var.password_length <= 99
    error_message = "password_length must be between 16 and 99."
  }
}
