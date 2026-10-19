# Plans the apps root against a mock AWS provider and an overridden identity-root state: no
# credentials, no backend and no state bucket.
#   terraform -chdir=infra/envs/poc/apps init -backend=false
#   terraform -chdir=infra/envs/poc/apps test
#
# Expected values come from the files the root answers to, never from a copy here: the app
# definitions in fixtures/apps/, the onboarding definition, contracts/registry.schema.json and
# contracts/resource-tags.md.

mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "111122223333"
    }
  }

  mock_resource "aws_cognito_user_pool_client" {
    defaults = {
      id = "1example23456789"
    }
  }

  mock_resource "aws_ssm_parameter" {
    defaults = {
      version = 1
    }
  }
}

# The identity root's outputs, in the shape infra/envs/poc/idp/outputs.tf gives them. The
# resource servers are fixtures/cognito/pool.json's, written out because Terraform 1.11 (CI's floor)
# allows no function calls in override_data. The first run asserts they still match the fixture, so
# a scope added to pool.json fails here until it is added here too.
override_data {
  target = data.terraform_remote_state.idp
  values = {
    outputs = {
      user_pool_id = "us-east-1_EXAMPLE"
      resource_servers = {
        "https://orders.spa-poc.test"  = ["https://orders.spa-poc.test/read", "https://orders.spa-poc.test/admin"]
        "https://reports.spa-poc.test" = ["https://reports.spa-poc.test/read", "https://reports.spa-poc.test/admin"]
      }
    }
  }
}

variables {
  # Every definition in fixtures/apps/, read here the way the root reads it.
  fixture_apps = {
    for f in fileset("../../../../fixtures/apps", "*.json") :
    jsondecode(file("../../../../fixtures/apps/${f}")).appId => jsondecode(file("../../../../fixtures/apps/${f}"))
  }
}

run "one_client_branding_style_and_parameter_per_definition" {
  command = plan

  assert {
    condition = data.terraform_remote_state.idp.outputs.resource_servers == {
      for rs in jsondecode(file("../../../../fixtures/cognito/pool.json")).resourceServers :
      rs.identifier => [for s in rs.scopes : "${rs.identifier}/${s.name}"]
    }
    error_message = "The overridden resource servers no longer match fixtures/cognito/pool.json; copy the change into this file's override_data."
  }

  assert {
    condition     = toset(keys(aws_cognito_user_pool_client.app)) == toset(keys(var.fixture_apps))
    error_message = "The root must create one client per definition in fixtures/apps/, and no other."
  }

  assert {
    condition     = toset(keys(aws_cognito_managed_login_branding.app)) == toset(keys(var.fixture_apps))
    error_message = "The root must create one managed-login branding style per definition, and no other."
  }

  assert {
    condition     = toset(keys(aws_ssm_parameter.client_secret)) == toset(keys(var.fixture_apps))
    error_message = "The root must create one secret parameter per definition, and no other."
  }
}

run "clients_are_confidential_code_grant_only_with_urls_from_host" {
  command = plan

  assert {
    condition     = alltrue([for c in aws_cognito_user_pool_client.app : c.generate_secret])
    error_message = "Every client is confidential: it has a secret."
  }

  assert {
    condition = alltrue([for c in aws_cognito_user_pool_client.app : (
      toset(c.allowed_oauth_flows) == toset(["code"]) && c.allowed_oauth_flows_user_pool_client
    )])
    error_message = "Every client is code grant only."
  }

  assert {
    condition = alltrue([for id, app in var.fixture_apps : (
      toset(aws_cognito_user_pool_client.app[id].callback_urls) == toset(["https://${app.host}/signin-oidc"])
    )])
    error_message = "The callback URL is https://<host>/signin-oidc, port included (docs/plan/01-plan.md, the port assumption)."
  }

  assert {
    condition = alltrue([for id, app in var.fixture_apps : (
      toset(aws_cognito_user_pool_client.app[id].logout_urls) == toset(["https://${app.host}/"])
    )])
    error_message = "The sign-out URL is https://<host>/, never /signout-callback-oidc (R-C7)."
  }

  assert {
    condition = alltrue([for id, app in var.fixture_apps : (
      toset(aws_cognito_user_pool_client.app[id].allowed_oauth_scopes) == toset(app.oauth.scopes)
    )])
    error_message = "A client is granted exactly its definition's scopes."
  }

  assert {
    condition     = alltrue([for c in aws_cognito_user_pool_client.app : toset(c.supported_identity_providers) == toset(["COGNITO"])])
    error_message = "Users sign in to the pool itself."
  }
}

run "tokens_rotation_and_revocation" {
  command = plan

  assert {
    condition = alltrue([for c in aws_cognito_user_pool_client.app : (
      c.access_token_validity == 5 && c.token_validity_units[0].access_token == "minutes"
    )])
    error_message = "Access tokens last 5 minutes, so refresh happens during a run (docs/plan/01-plan.md, gate item 2)."
  }

  # The longest session the registry allows, read from the contract, not repeated here.
  assert {
    condition = alltrue([for c in aws_cognito_user_pool_client.app : (
      c.refresh_token_validity * lookup({ minutes = 1, hours = 60, days = 1440 }, c.token_validity_units[0].refresh_token)
      > jsondecode(file("../../../../contracts/registry.schema.json"))["$defs"].session.properties.lifetimeMinutes.maximum
    )])
    error_message = "The refresh token must outlive the longest session contracts/registry.schema.json allows."
  }

  assert {
    condition = alltrue([for c in aws_cognito_user_pool_client.app : (
      c.refresh_token_rotation[0].feature == "ENABLED" && c.refresh_token_rotation[0].retry_grace_period_seconds == 10
    )])
    error_message = "Refresh rotation is on, with a 10-second grace."
  }

  assert {
    condition     = alltrue([for c in aws_cognito_user_pool_client.app : c.enable_token_revocation])
    error_message = "Token revocation is on: logout revokes the refresh token."
  }

  assert {
    condition     = alltrue([for c in aws_cognito_user_pool_client.app : !contains(tolist(c.explicit_auth_flows), "ALLOW_REFRESH_TOKEN_AUTH") && length(c.explicit_auth_flows) > 0])
    error_message = "ALLOW_REFRESH_TOKEN_AUTH is off, and the flows are explicit, since Cognito's default list includes it."
  }
}

run "parameter_value_changes_are_ignored" {
  command = plan

  assert {
    condition     = alltrue([for p in aws_ssm_parameter.client_secret : p.type == "SecureString"])
    error_message = "The client secret is a SecureString."
  }

  assert {
    condition = alltrue([for id, _ in var.fixture_apps : (
      aws_ssm_parameter.client_secret[id].name == "/spa-platform/poc/apps/${id}/client-secret"
    )])
    error_message = "The parameter sits under the cell's path, which the task role and the CI plan role read."
  }

  # terraform test cannot see a lifecycle block, so this reads the root's own source: a guard on
  # the one line that keeps a rotation from being undone (docs/plan/05, Rotating a client secret).
  assert {
    condition     = length(aws_ssm_parameter.client_secret) > 0 && can(regex("(?s)resource \"aws_ssm_parameter\" \"client_secret\".*?lifecycle \\{\\s*ignore_changes = \\[value\\]", file("main.tf")))
    error_message = "Terraform must ignore later changes to the parameter's value."
  }
}

run "outputs_follow_the_infra_outputs_contract" {
  command = apply

  assert {
    condition     = toset(keys(output.clients)) == toset(keys(var.fixture_apps))
    error_message = "clients is keyed by appId, one entry per definition."
  }

  # The keys of contracts/infra-outputs.schema.json -> clients' entries, read from the contract.
  assert {
    condition = alltrue([for c in output.clients : (
      toset(keys(c)) == toset(jsondecode(file("../../../../contracts/infra-outputs.schema.json")).properties.clients.additionalProperties.required)
    )])
    error_message = "Each clients entry has exactly the fields contracts/infra-outputs.schema.json requires."
  }

  assert {
    condition     = alltrue([for c in output.clients : c.clientId == "1example23456789" && c.secretVersion == 1])
    error_message = "clientId and secretVersion come from the client and the parameter."
  }
}

run "tags_are_the_ones_the_contract_names" {
  command = plan

  assert {
    condition     = local.tags.Platform == regex("\\| `Platform` \\| `([^`]+)` \\|", file("../../../../contracts/resource-tags.md"))[0]
    error_message = "Platform must be the platform tag in contracts/resource-tags.md."
  }

  assert {
    condition     = local.tags.Cell == var.cell && toset(keys(local.tags)) == toset(["Cell", "ManagedBy", "Platform"])
    error_message = "The root applies the contract's three tags and no others."
  }
}

# P-02: "A new app adds one Cognito client, one branding style and one parameter, nothing else"
# (docs/plan/04-tests.md). ADR-0016 decision 2 makes it a comparison of configuration plans: the
# fixture definitions, which every run above plans, then the same with one more, asserting exactly
# what is added and that the existing apps are unchanged. The run reads
# fixtures/onboarding/app3.json itself.
run "a_third_definition_adds_exactly_three_resources" {
  command = plan

  variables {
    definition_dirs = ["../../../../fixtures/apps", "../../../../fixtures/onboarding"]
    # The onboarding definition's own appId, read from the file rather than repeated here.
    onboarded_app = jsondecode(file("../../../../fixtures/onboarding/app3.json")).appId
  }

  assert {
    condition = (
      !contains(keys(var.fixture_apps), var.onboarded_app)
      && toset(keys(aws_cognito_user_pool_client.app)) == setunion(keys(var.fixture_apps), [var.onboarded_app])
      && toset(keys(aws_cognito_managed_login_branding.app)) == setunion(keys(var.fixture_apps), [var.onboarded_app])
      && toset(keys(aws_ssm_parameter.client_secret)) == setunion(keys(var.fixture_apps), [var.onboarded_app])
    )
    error_message = "A third definition adds exactly its client, branding style and parameter (P-02)."
  }

  # One of each per definition file. merge() keeps the last of two definitions that share an appId
  # across the directories, so a second file naming app2 would change app2's client rather than add
  # one, with the keys above still matching.
  assert {
    condition = alltrue([
      for n in [
        length(aws_cognito_user_pool_client.app),
        length(aws_cognito_managed_login_branding.app),
        length(aws_ssm_parameter.client_secret),
      ] : n == length(flatten([for dir in var.definition_dirs : fileset(dir, "*.json")]))
    ])
    error_message = "The root plans one client, one branding style and one parameter per definition file in definition_dirs. Fewer means two files share an appId, and the later one has replaced an existing app's definition (P-02)."
  }

  # "Unchanged existing ones" (ADR-0016 decision 2). A run reads only an earlier run's outputs, never
  # its plan, so this holds each existing app's resources to what its own definition gives, in the
  # plan that adds the third: the values the runs above check in the plan without it.
  assert {
    condition = alltrue([
      for id, app in var.fixture_apps : (
        aws_cognito_user_pool_client.app[id].name == "spa-platform-${var.cell}-${id}"
        && aws_cognito_user_pool_client.app[id].user_pool_id == data.terraform_remote_state.idp.outputs.user_pool_id
        && toset(aws_cognito_user_pool_client.app[id].callback_urls) == toset(["https://${app.host}/signin-oidc"])
        && toset(aws_cognito_user_pool_client.app[id].logout_urls) == toset(["https://${app.host}/"])
        && toset(aws_cognito_user_pool_client.app[id].allowed_oauth_scopes) == toset(app.oauth.scopes)
        && aws_cognito_managed_login_branding.app[id].user_pool_id == data.terraform_remote_state.idp.outputs.user_pool_id
        && aws_ssm_parameter.client_secret[id].name == "/spa-platform/${var.cell}/apps/${id}/client-secret"
      )
    ])
    error_message = "Onboarding a definition leaves every existing app's client, branding style and parameter as its own definition gives them (ADR-0016 decision 2, P-02)."
  }

  # The keys above are the whole plan only while the root's resources are those three maps: a
  # fourth resource block, in any file of the root, fails here. A name is whatever sits between the
  # quotes, since Terraform accepts capitals and hyphens in one; a pattern of lower case and
  # underscores missed resource "aws_ssm_parameter" "Extra-Secret" with every run green.
  assert {
    condition = length(aws_cognito_user_pool_client.app) > 0 && toset(regexall(
      "(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"",
      join("\n", [for f in fileset(".", "*.tf") : file(f)])
      )) == toset([
      ["aws_cognito_user_pool_client", "app"],
      ["aws_cognito_managed_login_branding", "app"],
      ["aws_ssm_parameter", "client_secret"],
    ])
    error_message = "The apps root may hold only the three per-definition resources (P-02)."
  }

  # Both patterns here find a declaration where it starts a line, and `terraform fmt -check` passes a
  # block comment in front of the keyword: /* rationale */ resource "aws_s3_bucket" "x" {} passed the
  # platform root's runs, which share these patterns. So a line that names a resource or module block
  # names it at its start, or is a # or // comment.
  assert {
    condition = length(aws_cognito_user_pool_client.app) > 0 && alltrue([
      for line in regexall("(?m)^[^\\n]*\\b(?:resource|module)\\s+\"[^\\n]*$", join("\n", [for f in fileset(".", "*.tf") : file(f)])) :
      can(regex("^(?:(?:resource|module)\\s|\\s*(?:#|//))", line))
    ])
    error_message = "Every resource and module block in the apps root starts its own line. A declaration behind a block comment, /* ... */ resource, is in the plan where no pattern here reads it (P-02)."
  }

  # And the three blocks are the root's whole configuration only while nothing else can add to the
  # plan: a module call brings in resources of any type, and Terraform loads *.tf.json beside *.tf
  # (https://developer.hashicorp.com/terraform/language/files), where no pattern here reads.
  assert {
    condition = (
      length(aws_cognito_user_pool_client.app) > 0
      && length(fileset(".", "*.tf.json")) == 0
      && length(regexall("(?m)^module\\s+\"", join("\n", [for f in fileset(".", "*.tf") : file(f)]))) == 0
    )
    error_message = "The apps root calls no module and has no .tf.json file: either adds resources that no assertion here reads (P-02)."
  }
}

run "a_scope_no_resource_server_offers_is_refused" {
  command = plan

  override_data {
    target = data.terraform_remote_state.idp
    values = {
      outputs = {
        user_pool_id     = "us-east-1_EXAMPLE"
        resource_servers = {}
      }
    }
  }

  expect_failures = [aws_cognito_user_pool_client.app]
}
