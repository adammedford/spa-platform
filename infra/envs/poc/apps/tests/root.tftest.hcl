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

  # Every argument the root sets on each app's three resources, per existing appId: what the plan
  # without a third definition gives, and so what the plan with one must give too (ADR-0016
  # decision 2: "unchanged existing ones"). The run before P-02's holds the plan to these values and
  # holds this map to the root's arguments, so an argument added to main.tf without an entry here
  # fails there. The arguments that depend on the cell or the pool (the names, the description and
  # user_pool_id) are added in the runs, because Terraform 1.11 lets a file-level variable refer to
  # no other variable. Two are read differently, and the run says how: the branding style's
  # client_id and the parameter's value are the client's computed id and secret, unknown at plan.
  # Built from fixtures/apps/ as fixture_apps is, since no variable here may read that one.
  existing_app_resources = {
    for app in [for f in fileset("../../../../fixtures/apps", "*.json") : jsondecode(file("../../../../fixtures/apps/${f}"))] :
    app.appId => {
      client = {
        generate_secret                      = true
        allowed_oauth_flows_user_pool_client = true
        allowed_oauth_flows                  = toset(["code"])
        allowed_oauth_scopes                 = toset(app.oauth.scopes)
        supported_identity_providers         = toset(["COGNITO"])
        callback_urls                        = toset(["https://${app.host}/signin-oidc"])
        logout_urls                          = toset(["https://${app.host}/"])
        access_token_validity                = 5
        id_token_validity                    = 5
        # The longest session contracts/registry.schema.json allows, plus the root's hour of margin.
        refresh_token_validity        = jsondecode(file("../../../../contracts/registry.schema.json"))["$defs"].session.properties.lifetimeMinutes.maximum + 60
        token_validity_units          = { access_token = "minutes", id_token = "minutes", refresh_token = "minutes" }
        refresh_token_rotation        = { feature = "ENABLED", retry_grace_period_seconds = 60 }
        enable_token_revocation       = true
        prevent_user_existence_errors = "ENABLED"
        explicit_auth_flows           = toset(["ALLOW_USER_SRP_AUTH"])
      }
      branding = {
        use_cognito_provided_values = true
      }
      parameter = {
        type = "SecureString"
        tier = "Standard"
      }
    }
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

  # Rotation on, with a 60-second grace, the most Cognito allows (#332 D1). The predicate is written
  # once and read twice: against each planned client, which it must accept, and against that same
  # client with any other grace or with rotation off, which it must refuse. The other graces are 0,
  # the old 10, and 59 and 61 either side of the value, so a client with any other value fails this
  # assert, and a predicate loosened to a bound (>= 10, <= 60) fails it too, rather than the assert
  # passing by construction.
  assert {
    condition = length(aws_cognito_user_pool_client.app) > 0 && alltrue(flatten([
      for c in aws_cognito_user_pool_client.app : [
        for probe in concat(
          [{ rotation = c.refresh_token_rotation[0], accepted = true }],
          [for g in [0, 10, 59, 61] : { rotation = merge(c.refresh_token_rotation[0], { retry_grace_period_seconds = g }), accepted = false }],
          [{ rotation = merge(c.refresh_token_rotation[0], { feature = "DISABLED" }), accepted = false }],
        ) : (probe.rotation.feature == "ENABLED" && probe.rotation.retry_grace_period_seconds == 60) == probe.accepted
      ]
    ]))
    error_message = "Every client's refresh rotation is on with retry_grace_period_seconds = 60 (#332 D1), and the predicate refuses the same client at any other grace or with rotation off."
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

# Each existing app's three resources as the plan without a third definition gives them, argument
# by argument: the values P-02's run holds the same resources to in the plan with one.
run "every_argument_of_each_app_s_resources_is_the_one_existing_app_resources_names" {
  command = plan

  assert {
    condition = alltrue([
      for id, expected in var.existing_app_resources : (
        {
          name                                 = aws_cognito_user_pool_client.app[id].name
          user_pool_id                         = aws_cognito_user_pool_client.app[id].user_pool_id
          generate_secret                      = aws_cognito_user_pool_client.app[id].generate_secret
          allowed_oauth_flows_user_pool_client = aws_cognito_user_pool_client.app[id].allowed_oauth_flows_user_pool_client
          allowed_oauth_flows                  = toset(aws_cognito_user_pool_client.app[id].allowed_oauth_flows)
          allowed_oauth_scopes                 = toset(aws_cognito_user_pool_client.app[id].allowed_oauth_scopes)
          supported_identity_providers         = toset(aws_cognito_user_pool_client.app[id].supported_identity_providers)
          callback_urls                        = toset(aws_cognito_user_pool_client.app[id].callback_urls)
          logout_urls                          = toset(aws_cognito_user_pool_client.app[id].logout_urls)
          access_token_validity                = aws_cognito_user_pool_client.app[id].access_token_validity
          id_token_validity                    = aws_cognito_user_pool_client.app[id].id_token_validity
          refresh_token_validity               = aws_cognito_user_pool_client.app[id].refresh_token_validity
          token_validity_units                 = aws_cognito_user_pool_client.app[id].token_validity_units[0]
          refresh_token_rotation               = aws_cognito_user_pool_client.app[id].refresh_token_rotation[0]
          enable_token_revocation              = aws_cognito_user_pool_client.app[id].enable_token_revocation
          prevent_user_existence_errors        = aws_cognito_user_pool_client.app[id].prevent_user_existence_errors
          explicit_auth_flows                  = toset(aws_cognito_user_pool_client.app[id].explicit_auth_flows)
          } == merge(expected.client, {
            name         = "spa-platform-${var.cell}-${id}"
            user_pool_id = data.terraform_remote_state.idp.outputs.user_pool_id
        })
        && {
          user_pool_id                = aws_cognito_managed_login_branding.app[id].user_pool_id
          use_cognito_provided_values = aws_cognito_managed_login_branding.app[id].use_cognito_provided_values
        } == merge(expected.branding, { user_pool_id = data.terraform_remote_state.idp.outputs.user_pool_id })
        && {
          name        = aws_ssm_parameter.client_secret[id].name
          description = aws_ssm_parameter.client_secret[id].description
          type        = aws_ssm_parameter.client_secret[id].type
          tier        = aws_ssm_parameter.client_secret[id].tier
          } == merge(expected.parameter, {
            name        = "/spa-platform/${var.cell}/apps/${id}/client-secret"
            description = "OAuth client secret for app ${id} of cell ${var.cell}. Read it; never copy it into a file."
        })
      )
    ])
    error_message = "An existing app's client, branding style or parameter differs from existing_app_resources in this file. Change the map in the same change as main.tf, and say why."
  }

  # The map names every argument main.tf sets on the three blocks: the names read from the source,
  # less the meta-arguments, are its keys and the ones added above. The two computed references are
  # held to their expressions in the source instead: the branding style names its own app's
  # client, and the parameter's value is that client's secret, which ignore_changes keeps an apply
  # from writing again once the parameter exists (main.tf), so no later plan can change it.
  assert {
    condition = alltrue([
      for block in [
        ["aws_cognito_user_pool_client", "app", concat(keys(values(var.existing_app_resources)[0].client), ["name", "user_pool_id"])],
        ["aws_cognito_managed_login_branding", "app", concat(keys(values(var.existing_app_resources)[0].branding), ["user_pool_id", "client_id"])],
        ["aws_ssm_parameter", "client_secret", concat(keys(values(var.existing_app_resources)[0].parameter), ["name", "description", "value"])],
        ] : setsubtract([
          for m in regexall("(?m)^  ([a-z_]+)\\s*[={]", regex("(?ms)^resource\\s+\"${block[0]}\"\\s+\"${block[1]}\"\\s*\\{(.*?)^\\}", file("main.tf"))[0]) : m[0]
      ], ["for_each", "lifecycle"]) == toset(block[2])
    ])
    error_message = "main.tf sets an argument on an app's client, branding style or parameter that existing_app_resources does not hold, so P-02 would not see onboarding change it. Add it to the map."
  }

  assert {
    condition = (
      length(aws_cognito_managed_login_branding.app) > 0
      && can(regex("(?m)^  client_id\\s*= aws_cognito_user_pool_client\\.app\\[each\\.key\\]\\.id$", regex("(?ms)^resource\\s+\"aws_cognito_managed_login_branding\"\\s+\"app\"\\s*\\{(.*?)^\\}", file("main.tf"))[0]))
      && can(regex("(?m)^  value\\s*= aws_cognito_user_pool_client\\.app\\[each\\.key\\]\\.client_secret$", regex("(?ms)^resource\\s+\"aws_ssm_parameter\"\\s+\"client_secret\"\\s*\\{(.*?)^\\}", file("main.tf"))[0]))
    )
    error_message = "Each app's branding style names that app's own client, and its parameter holds that client's secret."
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
  # its plan, so this holds every argument of each existing app's resources, in the plan that adds
  # the third, to existing_app_resources: the values the run before this one holds the plan without
  # it to, and which that run shows name every argument main.tf sets. A selection of them missed
  # allowed_oauth_flows = length(local.apps) > 2 ? ["implicit"] : ["code"] with every run green.
  assert {
    condition = alltrue([
      for id, expected in var.existing_app_resources : (
        {
          name                                 = aws_cognito_user_pool_client.app[id].name
          user_pool_id                         = aws_cognito_user_pool_client.app[id].user_pool_id
          generate_secret                      = aws_cognito_user_pool_client.app[id].generate_secret
          allowed_oauth_flows_user_pool_client = aws_cognito_user_pool_client.app[id].allowed_oauth_flows_user_pool_client
          allowed_oauth_flows                  = toset(aws_cognito_user_pool_client.app[id].allowed_oauth_flows)
          allowed_oauth_scopes                 = toset(aws_cognito_user_pool_client.app[id].allowed_oauth_scopes)
          supported_identity_providers         = toset(aws_cognito_user_pool_client.app[id].supported_identity_providers)
          callback_urls                        = toset(aws_cognito_user_pool_client.app[id].callback_urls)
          logout_urls                          = toset(aws_cognito_user_pool_client.app[id].logout_urls)
          access_token_validity                = aws_cognito_user_pool_client.app[id].access_token_validity
          id_token_validity                    = aws_cognito_user_pool_client.app[id].id_token_validity
          refresh_token_validity               = aws_cognito_user_pool_client.app[id].refresh_token_validity
          token_validity_units                 = aws_cognito_user_pool_client.app[id].token_validity_units[0]
          refresh_token_rotation               = aws_cognito_user_pool_client.app[id].refresh_token_rotation[0]
          enable_token_revocation              = aws_cognito_user_pool_client.app[id].enable_token_revocation
          prevent_user_existence_errors        = aws_cognito_user_pool_client.app[id].prevent_user_existence_errors
          explicit_auth_flows                  = toset(aws_cognito_user_pool_client.app[id].explicit_auth_flows)
          } == merge(expected.client, {
            name         = "spa-platform-${var.cell}-${id}"
            user_pool_id = data.terraform_remote_state.idp.outputs.user_pool_id
        })
        && {
          user_pool_id                = aws_cognito_managed_login_branding.app[id].user_pool_id
          use_cognito_provided_values = aws_cognito_managed_login_branding.app[id].use_cognito_provided_values
        } == merge(expected.branding, { user_pool_id = data.terraform_remote_state.idp.outputs.user_pool_id })
        && {
          name        = aws_ssm_parameter.client_secret[id].name
          description = aws_ssm_parameter.client_secret[id].description
          type        = aws_ssm_parameter.client_secret[id].type
          tier        = aws_ssm_parameter.client_secret[id].tier
          } == merge(expected.parameter, {
            name        = "/spa-platform/${var.cell}/apps/${id}/client-secret"
            description = "OAuth client secret for app ${id} of cell ${var.cell}. Read it; never copy it into a file."
        })
      )
    ])
    error_message = "Onboarding a definition leaves every argument of every existing app's client, branding style and parameter as it is without it (ADR-0016 decision 2, P-02)."
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
  #
  # That exemption holds only while no /* */ comment exists: inside one a line can start with #, and
  # /*<newline># rationale */ resource "aws_s3_bucket" "x" {} passed the platform root's runs. The
  # argument guard in the run before this one reads an argument where it starts a line too, and
  # /* explanation */ auth_session_validity = ... on the client escaped it with every run green. The
  # platform root's tests say why these files refuse */ rather than strip block comments.
  assert {
    condition     = length(aws_cognito_user_pool_client.app) > 0 && !strcontains(join("\n", [for f in fileset(".", "*.tf") : file(f)]), "*/")
    error_message = "The apps root holds no /* */ comment: one can end in front of a declaration or an argument on the same line, where no pattern here reads it, and inside one a line can start with #. Write the comment with # (P-02)."
  }

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
