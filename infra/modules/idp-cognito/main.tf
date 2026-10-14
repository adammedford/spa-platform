# The cell's user pool: one issuer for every app (assumption A4), with its managed-login domain,
# resource servers and test users. App clients are not here: they belong to the apps root
# (docs/plan/05-infrastructure-and-cost.md -> Terraform boundaries), so onboarding an app never
# plans a change to the pool.
#
# Tags come from the calling root's provider default_tags (contracts/resource-tags.md). Of the
# resources below, the pool and the parameters accept tags. The domain, the resource servers and
# the users accept none, and each is a child of the pool, so destroying the tagged pool removes
# them: the rule that keeps teardown's tag query a proof that the account is empty.

locals {
  name = "spa-platform-${var.cell}"

  users = { for u in var.users : u.username => u }

  # The test-user passwords live under the cell's Parameter Store path, which is what the CI
  # plan role may read (infra/bootstrap/ci-role.tf, ReadCellParameters) and the shape of
  # contracts/infra-outputs.example.json -> testUsers.
  password_parameter = { for name, _ in local.users : name => "/spa-platform/${var.cell}/test-users/${name}" }
}

resource "aws_cognito_user_pool" "this" {
  name = local.name

  # Essentials is the tier managed login needs, and its first 10,000 monthly active users are
  # free (docs/plan/05-infrastructure-and-cost.md -> cost). Lite has no managed login.
  # https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-sign-in-feature-plans.html
  user_pool_tier = "ESSENTIALS"

  # Teardown destroys this root and P-05 then asserts the platform tag answers nothing, so the
  # pool must be destroyable without a manual step.
  deletion_protection = "INACTIVE"

  # Nobody signs up. The only users are the test users below, created by an administrator.
  admin_create_user_config {
    allow_admin_create_user_only = true
  }

  username_configuration {
    case_sensitive = false
  }

  # The generated passwords meet this with room to spare; it is here so a hand-set password
  # cannot be weaker than the generated ones.
  password_policy {
    minimum_length                   = 16
    require_lowercase                = true
    require_uppercase                = true
    require_numbers                  = true
    require_symbols                  = true
    temporary_password_validity_days = 1
  }

  # The browser suite signs the test users in unattended, so no second factor. Recovery is by
  # an administrator only: no message ever leaves the pool.
  mfa_configuration = "OFF"

  account_recovery_setting {
    recovery_mechanism {
      name     = "admin_only"
      priority = 1
    }
  }
}

# A prefix domain, <prefix>.auth.<region>.amazoncognito.com, so no certificate and no DNS record
# to own. managed_login_version 2 serves managed login rather than the classic hosted UI; only
# managed login takes the per-client branding styles task 1.8 creates.
# https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-assign-domain-prefix.html
resource "aws_cognito_user_pool_domain" "this" {
  domain                = var.domain_prefix
  user_pool_id          = aws_cognito_user_pool.this.id
  managed_login_version = 2
}

# Keyed by identifier, which is what an access token's scope names (<identifier>/<scope>) and
# what the apps root's clients ask for, so renaming one is a replacement, as it should be.
resource "aws_cognito_resource_server" "this" {
  for_each = { for rs in var.resource_servers : rs.identifier => rs }

  user_pool_id = aws_cognito_user_pool.this.id
  identifier   = each.key
  name         = each.value.name

  dynamic "scope" {
    for_each = each.value.scopes

    content {
      scope_name        = scope.value.name
      scope_description = scope.value.description
    }
  }
}

# One password per test user. The value is in state, because aws_cognito_user's password is not
# a write-only attribute in hashicorp/aws 6.x; state is encrypted, TLS only and private
# (infra/bootstrap/state.tf). It is never an output and never logged: readers take the parameter
# name from the outputs and read the value from Parameter Store.
resource "random_password" "test_user" {
  for_each = local.users

  length      = var.password_length
  min_lower   = 1
  min_upper   = 1
  min_numeric = 1
  min_special = 1

  # Every character here is in Cognito's allowed symbol set, and none needs escaping in a shell,
  # a JSON document or a form field.
  # https://docs.aws.amazon.com/cognito/latest/developerguide/managing-users-passwords.html
  override_special = "!#%*-_=+"
}

# message_action SUPPRESS sends no invitation, and setting `password` makes the user CONFIRMED
# with a permanent password (AdminSetUserPassword, Permanent = true), so the first sign-in is a
# plain one with no forced change.
# https://registry.terraform.io/providers/hashicorp/aws/latest/docs/resources/cognito_user
resource "aws_cognito_user" "this" {
  for_each = local.users

  user_pool_id   = aws_cognito_user_pool.this.id
  username       = each.key
  password       = random_password.test_user[each.key].result
  message_action = "SUPPRESS"

  attributes = {
    email          = each.value.email
    email_verified = "true"
    name           = each.value.name
  }
}

# SecureString under the AWS-managed key alias/aws/ssm: encrypted with no key to manage and no
# monthly key charge. A standard-tier parameter is free (docs/plan/05-infrastructure-and-cost.md).
resource "aws_ssm_parameter" "test_user_password" {
  for_each = local.users

  name        = local.password_parameter[each.key]
  description = "Generated password for the test user ${each.key} of cell ${var.cell}. Read it; never copy it into a file."
  type        = "SecureString"
  tier        = "Standard"
  value       = random_password.test_user[each.key].result
}

data "aws_region" "current" {}
