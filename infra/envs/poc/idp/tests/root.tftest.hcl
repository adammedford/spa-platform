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
