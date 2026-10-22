# Plans the root against a mock AWS provider: no credentials and no backend.
#   terraform -chdir=infra/envs/poc/idp init -backend=false
#   terraform -chdir=infra/envs/poc/idp test

mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "111122223333"
    }
  }
}

mock_provider "random" {}

run "creates_the_resource_servers_and_users_the_fixture_lists" {
  command = apply

  # Read from the fixture rather than repeated here, so a user or server added to pool.json is
  # expected without editing this test.
  assert {
    condition = (
      sort(keys(module.idp.resource_servers))
      == sort([for rs in jsondecode(file("../../../../fixtures/cognito/pool.json")).resourceServers : rs.identifier])
    )
    error_message = "The root must create every resource server in fixtures/cognito/pool.json, and no other."
  }

  assert {
    condition = (
      sort(keys(output.test_users))
      == sort([for u in jsondecode(file("../../../../fixtures/cognito/pool.json")).users : u.username])
    )
    error_message = "The root must create every test user in fixtures/cognito/pool.json, and no other."
  }
}

run "tags_are_the_ones_the_contract_names" {
  command = plan

  # Each row of the contract's tables reads | `Key` | `value` | ... . The first Platform row is
  # the platform tag; a later one is the bootstrap exemption, which this root must not carry.
  # The cell's value is the variable: the contract's "e.g. `poc`" is not a fixed value.
  assert {
    condition     = local.tags.Platform == regex("\\| `Platform` \\| `([^`]+)` \\|", file("../../../../contracts/resource-tags.md"))[0]
    error_message = "Platform must be the platform tag in contracts/resource-tags.md: teardown and P-05 match it exactly."
  }

  assert {
    condition     = local.tags.Cell == var.cell
    error_message = "Cell must be the cell name (contracts/resource-tags.md)."
  }

  assert {
    condition     = strcontains(file("../../../../contracts/resource-tags.md"), "| `ManagedBy` | `${local.tags.ManagedBy}`")
    error_message = "ManagedBy must be a value contracts/resource-tags.md names."
  }

  assert {
    condition     = toset(keys(local.tags)) == toset(["Cell", "ManagedBy", "Platform"])
    error_message = "The root applies the contract's three tags and no others."
  }
}

run "domain_prefix_defaults_to_the_cell_and_account" {
  command = plan

  assert {
    condition     = output.cognito_domain_prefix == "spa-platform-poc-111122223333"
    error_message = "With no override, the prefix is spa-platform-<cell>-<account id>."
  }
}

# --- Task 1.9: the identity root's allowlist -----------------------------------------------------
#
# Plan row 1.1 is this root's whole plan: "a user pool on the Essentials plan, a prefix domain on
# managed login, and the resource servers and test users listed in fixtures/cognito/pool.json. Test
# passwords are generated into Parameter Store. No app clients." Row 1.9 holds it to that the way the
# platform root holds P-01 (infra/envs/poc/platform/tests/root.tftest.hcl): read from the source,
# because a plan holds no list of its resources that a test can enumerate, and planned against the
# mock providers above, because CI holds no AWS credentials (ADR-0016). Q42 in
# .scratch/overnight/questions-2026-10-15-answered.md, answered yes, put this root in row 1.9 with
# aws_cognito_user_pool_client named as absent: the clients are the apps root's, which is what keeps
# onboarding an app out of this root's plan (docs/plan/05-infrastructure-and-cost.md -> Terraform
# boundaries). The two runs below are P-07, the identity plan allowlist, which ADR-0016 decision 7
# registers because docs/plan/04-tests.md is a frozen export that takes no new row.
#
# Patterns take quoted labels, the only form `terraform fmt -check` passes, and paths are built on
# path.module because Terraform refuses an assertion that refers to nothing in the configuration.

run "the_identity_root_declares_no_resource_and_calls_the_idp_module_once_and_nothing_else" {
  command = plan

  # The next run finds the module's declarations with this same pattern.
  assert {
    condition = length(regexall(
      "(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"",
      join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
    )) == 0
    error_message = "The identity root declares no resource of its own: its plan is modules/idp-cognito's, which the next run holds to plan row 1.1."
  }

  # A block written on one line holds one argument, and `terraform fmt -check` passes it as it is.
  # The module pattern below and the next run's meta-argument pattern end a block at a closing brace
  # in the first column, so they read past a one-line block. Two such blocks passed every run here:
  # module "nat" { source = "./nat" } at the end of versions.tf, which put a NAT gateway in this
  # root's plan; and resource "random_pet" "extra" { count = 3 } in modules/idp-cognito, beside a map
  # entry of one.
  assert {
    condition = alltrue([
      for header in regexall("(?m)^(?:resource|module)\\s[^\\n]*$", join("\n", concat(
        [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
        [for f in fileset("${path.module}/../../../modules/idp-cognito", "*.tf") : file("${path.module}/../../../modules/idp-cognito/${f}")],
      ))) : endswith(trimspace(header), "{")
    ])
    error_message = "Every resource and module block in the identity root and modules/idp-cognito opens a block of its own lines. A one-line block carries its source, count or for_each where the patterns that read them never look."
  }

  # Every pattern in this file finds a declaration where it starts a line, and `terraform fmt -check`
  # passes a block comment in front of the keyword: /* rationale */ resource "aws_s3_bucket" "x" {}
  # passed the platform root's runs, which share these patterns. So a line that names a resource or
  # module block names it at its start, or is a # or // comment.
  #
  # That exemption holds only while no /* */ comment exists: inside one a line can start with #, and
  # /*<newline># rationale */ resource "aws_s3_bucket" "x" {} passed the platform root's runs. The
  # platform root's tests say why these files refuse */ rather than strip block comments.
  assert {
    condition = !strcontains(join("\n", concat(
      [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
      [for f in fileset("${path.module}/../../../modules/idp-cognito", "*.tf") : file("${path.module}/../../../modules/idp-cognito/${f}")],
    )), "*/")
    error_message = "The identity root and modules/idp-cognito hold no /* */ comment: one can end in front of a declaration on the same line, where no pattern here reads it, and inside one a line can start with #. Write the comment with #."
  }

  assert {
    condition = alltrue([
      for line in regexall("(?m)^[^\\n]*\\b(?:resource|module)\\s+\"[^\\n]*$", join("\n", concat(
        [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
        [for f in fileset("${path.module}/../../../modules/idp-cognito", "*.tf") : file("${path.module}/../../../modules/idp-cognito/${f}")],
      ))) : can(regex("^(?:(?:resource|module)\\s|\\s*(?:#|//))", line))
    ])
    error_message = "Every resource and module block in the identity root and modules/idp-cognito starts its own line. A declaration behind a block comment, /* ... */ resource, is in the plan where no pattern here reads it."
  }

  assert {
    condition = [
      for m in regexall("(?ms)^module\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])) :
      [m[0], try(regex("(?m)^\\s*source\\s*=\\s*\"([^\"]+)\"", m[1])[0], "")]
    ] == [["idp", "../../../modules/idp-cognito"]]
    error_message = "The identity root calls one module, idp, from ../../../modules/idp-cognito, and nothing else: another module call brings resources of any type into the plan."
  }

  assert {
    condition = alltrue([
      for m in regexall("(?ms)^module\\s+\"[^\"]+\"\\s*\\{(.*?)^\\}", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])) :
      length(regexall("(?m)^\\s*(count|for_each)\\s*=", m[0])) == 0
    ])
    error_message = "The idp module is called with no count or for_each: either multiplies the pool."
  }

  # Terraform loads *.tf.json beside *.tf (https://developer.hashicorp.com/terraform/language/files).
  assert {
    condition     = length(fileset(path.module, "*.tf.json")) == 0 && length(fileset("${path.module}/../../../modules/idp-cognito", "*.tf.json")) == 0
    error_message = "The identity root and modules/idp-cognito are written in .tf files only: a .tf.json file declares blocks that no source-reading assertion sees."
  }

  assert {
    condition = length(regexall(
      "(?m)^module\\s+\"",
      join("\n", [for f in fileset("${path.module}/../../../modules/idp-cognito", "*.tf") : file("${path.module}/../../../modules/idp-cognito/${f}")])
    )) == 0
    error_message = "modules/idp-cognito calls no child module, so its own .tf files are the whole of what it adds to the plan."
  }
}

# The counts are plan row 1.1's, with the fixture's numbers read from the fixture rather than
# repeated: one pool and one domain, a resource server per entry in pool.json's resourceServers, and
# for each test user a generated password, the user and the parameter the password is written to.
# Against the module, with the inputs the root gives it, because only a run against the module can
# address its for_each resources; every other declaration carries no count or for_each, so it is
# one instance.
run "the_identity_plan_holds_one_pool_one_domain_and_the_fixture_s_servers_and_users" {
  command = plan

  module {
    source = "../../../modules/idp-cognito"
  }

  variables {
    cell             = "poc"
    domain_prefix    = "spa-platform-poc-111122223333"
    resource_servers = jsondecode(file("../../../../fixtures/cognito/pool.json")).resourceServers
    users            = jsondecode(file("../../../../fixtures/cognito/pool.json")).users

    # modules/idp-cognito's source, read from the root's directory, which is where terraform test
    # resolves a relative path even in a run against a module.
    idp_source = join("\n", [for f in fileset("../../../modules/idp-cognito", "*.tf") : file("../../../modules/idp-cognito/${f}")])
  }

  # Two spaces is the canonical indent of a block's own argument, which tells a meta-argument from
  # the for_each of the resource servers' dynamic scope block.
  assert {
    condition = toset([
      for m in regexall("(?ms)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", var.idp_source) :
      "${m[0]}.${m[1]}" if length(regexall("(?m)^  (count|for_each)\\s*=", m[2])) > 0
      ]) == toset(keys({
        "aws_cognito_resource_server.this"     = aws_cognito_resource_server.this
        "random_password.test_user"            = random_password.test_user
        "aws_cognito_user.this"                = aws_cognito_user.this
        "aws_ssm_parameter.test_user_password" = aws_ssm_parameter.test_user_password
    }))
    error_message = "Only the resource servers and the three per-user resources may carry count or for_each: every other declaration is one instance, and that is how the next assertion counts it."
  }

  assert {
    condition = {
      for type in distinct([for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.idp_source) : m[0]]) :
      type => sum([
        for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.idp_source) :
        lookup({
          "aws_cognito_resource_server.this"     = length(aws_cognito_resource_server.this)
          "random_password.test_user"            = length(random_password.test_user)
          "aws_cognito_user.this"                = length(aws_cognito_user.this)
          "aws_ssm_parameter.test_user_password" = length(aws_ssm_parameter.test_user_password)
        }, "${m[0]}.${m[1]}", 1)
        if m[0] == type
      ])
      } == {
      aws_cognito_user_pool        = 1
      aws_cognito_user_pool_domain = 1
      aws_cognito_resource_server  = length(var.resource_servers)
      random_password              = length(var.users)
      aws_cognito_user             = length(var.users)
      aws_ssm_parameter            = length(var.users)
    }
    error_message = "The identity plan holds a type plan row 1.1 does not name, or a different number of one it does: one pool, one domain, the fixture's resource servers, and a password, a user and a parameter per test user."
  }

  # Named on its own, so an edit that adds a client and the map entry for it still fails.
  assert {
    condition     = !contains([for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.idp_source) : m[0]], "aws_cognito_user_pool_client")
    error_message = "No app clients in the identity root (plan row 1.1): each client is the apps root's, so onboarding an app plans no change to the pool."
  }
}
