provider "aws" {
  region = var.region

  default_tags {
    tags = local.tags
  }
}

data "aws_caller_identity" "current" {}

# The identity root's outputs: the pool every client is created in, and the scopes its resource
# servers offer. Read from state, as the platform root reads this root's client ids
# (docs/plan/05-infrastructure-and-cost.md -> Terraform boundaries), so the pool id is never
# copied by hand. The key is the identity root's backend key (infra/envs/poc/idp/versions.tf).
data "terraform_remote_state" "idp" {
  backend = "s3"

  config = {
    bucket = (
      var.state_bucket != ""
      ? var.state_bucket
      : "spa-platform-${var.cell}-tfstate-${data.aws_caller_identity.current.account_id}"
    )
    key    = "poc/idp/terraform.tfstate"
    region = var.region
  }
}

locals {
  # contracts/resource-tags.md: the three tags, through default_tags. Of what this root creates
  # only the parameters accept tags. Clients and branding styles accept none and are children of
  # the tagged user pool, so destroying the pool removes them (contracts/resource-tags.md ->
  # Resources that cannot be tagged).
  tags = {
    Platform  = "spa-platform"
    Cell      = var.cell
    ManagedBy = "terraform"
  }

  # One definition per file, keyed by appId. Each app is described once: Terraform reads the
  # definition to create the client, and spactl app add completes it from the outputs.
  apps = merge([
    for dir in var.definition_dirs : {
      for d in [for f in fileset(dir, "*.json") : jsondecode(file("${dir}/${f}"))] : d.appId => d
    }
  ]...)

  user_pool_id = data.terraform_remote_state.idp.outputs.user_pool_id

  # Scopes a client may ask for: the resource servers' full scope names, plus Cognito's own.
  # https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-define-resource-servers.html
  grantable_scopes = toset(concat(
    ["openid", "profile", "email", "phone", "aws.cognito.signin.user.admin"],
    flatten(values(data.terraform_remote_state.idp.outputs.resource_servers)),
  ))

  # The refresh token outlives the longest session the registry allows (plan row 1.8), so a
  # session never ends early for want of a refresh. Read from the contract: a longer maximum
  # there raises this without an edit here. One hour of margin; Cognito's floor is 60 minutes.
  longest_session_minutes = jsondecode(file("${path.module}/../../../../contracts/registry.schema.json"))["$defs"].session.properties.lifetimeMinutes.maximum
  refresh_token_minutes   = local.longest_session_minutes + 60
}

# One confidential client per app.
# https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_CreateUserPoolClient.html
resource "aws_cognito_user_pool_client" "app" {
  for_each = local.apps

  name         = "spa-platform-${var.cell}-${each.key}"
  user_pool_id = local.user_pool_id

  # Confidential and code grant only: the gateway holds the secret and the tokens (ADR-0001's
  # server-side tokens), and the browser never sees either.
  generate_secret                      = true
  allowed_oauth_flows_user_pool_client = true
  allowed_oauth_flows                  = ["code"]
  allowed_oauth_scopes                 = each.value.oauth.scopes
  supported_identity_providers         = ["COGNITO"]

  # The host carries the port (app1.spa-poc.test:8443), so the callback does too: Cognito
  # redirects back to the tunnel's local port (docs/plan/01-plan.md -> assumptions; G0 item 3).
  # /signin-oidc is the shared callback path (docs/plan/02-contracts.md -> reserved paths).
  callback_urls = ["https://${each.value.host}/signin-oidc"]

  # Cognito's logout returns no state, and /signout-callback-oidc answers an empty page without
  # one (R-C7), so the cognito profile lands users on / (plan row 3.7).
  logout_urls = ["https://${each.value.host}/"]

  # Five-minute access tokens, so refresh happens during a test run (docs/plan/01-plan.md ->
  # gate item 2). The ID token is read once, at the callback, and gets the same life. Units are
  # explicit: without this block the numbers would be hours, hours and days.
  access_token_validity  = 5
  id_token_validity      = 5
  refresh_token_validity = local.refresh_token_minutes

  token_validity_units {
    access_token  = "minutes"
    id_token      = "minutes"
    refresh_token = "minutes"
  }

  # Each refresh returns a new refresh token; the one it replaces still works for 10 seconds, so
  # two concurrent refreshes do not end a session (G0 item 2 asks what happens inside the window).
  # https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-refresh-token.html
  refresh_token_rotation {
    feature                    = "ENABLED"
    retry_grace_period_seconds = 10
  }

  # Logout revokes the refresh token (docs/plan/02-contracts.md -> POST /bff/logout).
  enable_token_revocation       = true
  prevent_user_existence_errors = "ENABLED"

  # Explicit, because CreateUserPoolClient's default list includes ALLOW_REFRESH_TOKEN_AUTH, which
  # refresh rotation rules out (plan row 1.8). The gateway signs in only through managed login
  # and the code grant; SRP is the one direct flow left, and it never sends the password.
  explicit_auth_flows = ["ALLOW_USER_SRP_AUTH"]

  lifecycle {
    precondition {
      condition     = alltrue([for s in each.value.oauth.scopes : contains(local.grantable_scopes, s)])
      error_message = "App ${each.key} asks for a scope no resource server in the identity root offers (fixtures/cognito/pool.json)."
    }
  }
}

# Managed login's default style, one per client: managed login shows a client's page only when
# the client has a style. Needs the pool's domain on managed_login_version 2 (the identity root).
# https://docs.aws.amazon.com/cognito/latest/developerguide/managed-login-branding.html
resource "aws_cognito_managed_login_branding" "app" {
  for_each = local.apps

  user_pool_id                = local.user_pool_id
  client_id                   = aws_cognito_user_pool_client.app[each.key].id
  use_cognito_provided_values = true
}

# The client secret, under the cell's parameter path, which the task role reads (plan row 1.4)
# and the CI plan role may read (infra/bootstrap/ci-role.tf, ReadCellParameters). Standard tier
# and the AWS-managed key: free, with no key to manage.
resource "aws_ssm_parameter" "client_secret" {
  for_each = local.apps

  name        = "/spa-platform/${var.cell}/apps/${each.key}/client-secret"
  description = "OAuth client secret for app ${each.key} of cell ${var.cell}. Read it; never copy it into a file."
  type        = "SecureString"
  tier        = "Standard"
  value       = aws_cognito_user_pool_client.app[each.key].client_secret

  # A rotation writes a new version by hand (docs/plan/05-infrastructure-and-cost.md -> Rotating
  # a client secret); the next apply must not put the old secret back.
  lifecycle {
    ignore_changes = [value]
  }
}
