# Plans the bootstrap root against a mock AWS provider: no credentials and no state.
#   terraform -chdir=infra/bootstrap init -backend=false
#   terraform -chdir=infra/bootstrap test
#
# Change request 1.5 and ADR-0014: the cell-name rule this root shares with the platform root, and
# the teardown role. Expected values come from the decision, not from this root's own locals.

mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "111122223333"
    }
  }

  mock_data "aws_partition" {
    defaults = {
      partition  = "aws"
      dns_suffix = "amazonaws.com"
    }
  }

  # The CI role's and the state bucket's policies are aws_iam_policy_document data sources, which a
  # mock provider would fill with a random string that the apply run's validation refuses.
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }

  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/spa-platform-poc-teardown"
    }
  }

  mock_resource "aws_iam_openid_connect_provider" {
    defaults = {
      arn = "arn:aws:iam::111122223333:oidc-provider/token.actions.githubusercontent.com"
    }
  }

  mock_resource "aws_s3_bucket" {
    defaults = {
      arn = "arn:aws:s3:::spa-platform-poc-tfstate-111122223333"
    }
  }

  mock_resource "aws_iam_policy" {
    defaults = {
      arn = "arn:aws:iam::111122223333:policy/spa-platform-poc-ci-plan"
    }
  }
}

variables {
  cell              = "poc"
  github_repository = "example/spa-platform"
  teardown_trusted_principal_arns = [
    "arn:aws:iam::111122223333:user/operator",
    "arn:aws:iam::111122223333:role/aws-reserved/sso.amazonaws.com/AWSReservedSSO_Operator_0123456789abcdef",
  ]
}

# --- The cell name (change request 1.5, decision item 3; ADR-0014 item 7) ------------------------
#
# 3 to 22 characters, no edge hyphen. The verdicts below are the decision's, written out by hand:
# `pc` is under the frozen appId minimum of 3, `poc-` would make a bucket name end in a hyphen, and
# 22 is the bound a landed platform-root test pins.

run "cell_accepts_the_shortest_name" {
  command = plan

  variables {
    cell = "abc"
  }
}

run "cell_accepts_the_longest_name" {
  command = plan

  variables {
    cell = "abcdefghijklmnopqrstuv"
  }
}

run "cell_accepts_an_inner_hyphen_and_a_leading_digit" {
  command = plan

  variables {
    cell = "0a-b"
  }
}

run "cell_refuses_pc" {
  command = plan

  variables {
    cell = "pc"
  }

  expect_failures = [var.cell]
}

run "cell_refuses_a_trailing_hyphen" {
  command = plan

  variables {
    cell = "poc-"
  }

  expect_failures = [var.cell]
}

run "cell_refuses_a_leading_hyphen" {
  command = plan

  variables {
    cell = "-poc"
  }

  expect_failures = [var.cell]
}

run "cell_refuses_23_characters" {
  command = plan

  variables {
    cell = "abcdefghijklmnopqrstuvw"
  }

  expect_failures = [var.cell]
}

run "cell_refuses_upper_case" {
  command = plan

  variables {
    cell = "Poc"
  }

  expect_failures = [var.cell]
}

# --- The rule is the same text in every place that validates a cell -------------------------------
#
# The runs above prove this root's verdicts. This one reads the source of the three validations, so
# the platform root (and the module it passes the cell to) cannot drift from this one unnoticed, and
# it checks every accepted probe against the frozen appId schema the cell is published as. The cell
# rule is deliberately stricter than appId (which allows edge hyphens and up to 40 characters), so
# only "accepted implies valid appId" is asserted, never identical verdicts.
#
# The last assertion is the mutation: the same extraction applied to this root's source with the
# lower bound widened back to {0,20} must no longer equal the decision, which shows the check can
# see drift rather than passing by construction.

run "cell_rule_is_identical_in_every_root_and_within_the_appid_schema" {
  command = plan

  module {
    source = "./tests/cell-rule"
  }

  variables {
    sources = {
      bootstrap = file("variables.tf")
      platform  = file("../envs/poc/platform/variables.tf")
      module    = file("../modules/cell/variables.tf")
      # The mutation: this root's source with the lower bound widened back to {0,20}.
      mutated = replace(file("variables.tf"), "{1,20}", "{0,20}")
    }
    registry_schema = file("../../contracts/registry.schema.json")
  }

  assert {
    condition = alltrue([
      for label in ["bootstrap", "platform", "module"] :
      output.rules[label] == "^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$"
    ])
    error_message = "infra/bootstrap, infra/envs/poc/platform and infra/modules/cell must each validate cell with exactly ^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$ (change request 1.5, ADR-0014 item 7)."
  }

  assert {
    condition = alltrue([
      for label in ["bootstrap", "platform", "module"] : alltrue(concat(
        [for p in ["abc", "poc", "0a-b", "abcdefghijklmnopqrstuv"] : can(regex(output.rules[label], p))],
        [for p in ["pc", "poc-", "-poc", "-ab", "ab-", "abcdefghijklmnopqrstuvw", "Poc", "p_c", ""] : !can(regex(output.rules[label], p))]
      ))
    ])
    error_message = "A cell-name probe got the wrong verdict from one of the three source validations."
  }

  assert {
    condition = alltrue([
      for p in ["abc", "poc", "0a-b", "abcdefghijklmnopqrstuv"] :
      length(p) >= output.app_id.minLength && length(p) <= output.app_id.maxLength && can(regex(output.app_id.pattern, p))
    ])
    error_message = "Every cell name the rule accepts must be a valid appId (contracts/registry.schema.json), which contracts/infra-outputs.schema.json publishes the cell as."
  }

  assert {
    condition     = output.rules["mutated"] != null && output.rules["mutated"] != "^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$" && can(regex(output.rules["mutated"], "pc"))
    error_message = "Mutation check: widening the lower bound in the source must make the extraction disagree with the decision and admit pc, or the first assertion proves nothing."
  }
}

# --- The teardown role (change request 1.5, decision item 1; ADR-0014) ----------------------------

run "teardown_role_is_named_from_the_cell" {
  command = plan

  assert {
    condition     = aws_iam_role.teardown.name == "spa-platform-poc-teardown"
    error_message = "The teardown role is spa-platform-<cell>-teardown, the name the platform root and teardown.sh derive (ADR-0014 item 2)."
  }
}

run "teardown_role_may_delete_and_list_the_platform_bucket_and_nothing_else" {
  command = plan

  assert {
    condition = jsondecode(aws_iam_role_policy.teardown.policy).Statement == [
      {
        Sid      = "DeleteReleases"
        Effect   = "Allow"
        Action   = ["s3:DeleteObject", "s3:DeleteObjectVersion"]
        Resource = "arn:aws:s3:::spa-platform-poc/apps/*"
      },
      {
        Sid      = "ListPlatformBucket"
        Effect   = "Allow"
        Action   = ["s3:ListBucket", "s3:ListBucketVersions"]
        Resource = "arn:aws:s3:::spa-platform-poc"
      },
    ]
    error_message = "The teardown role holds the delete under apps/ and the two lists on the platform bucket, on that bucket's ARN, and no write at all (ADR-0014 item 6)."
  }

}

run "teardown_role_follows_a_platform_bucket_name_override" {
  command = plan

  variables {
    platform_bucket_name = "spa-platform-poc-111122223333"
  }

  assert {
    condition = toset(flatten([for s in jsondecode(aws_iam_role_policy.teardown.policy).Statement : s.Resource])) == toset([
      "arn:aws:s3:::spa-platform-poc-111122223333/apps/*",
      "arn:aws:s3:::spa-platform-poc-111122223333",
    ])
    error_message = "The grant names the actual bucket, including an override, never a spa-platform-<cell>-* glob."
  }
}

run "teardown_trust_requires_mfa_for_users_and_names_roles_separately" {
  command = plan

  assert {
    condition = jsondecode(aws_iam_role.teardown.assume_role_policy).Statement == [
      {
        Sid       = "NamedUsersWithMfa"
        Effect    = "Allow"
        Action    = "sts:AssumeRole"
        Principal = { AWS = ["arn:aws:iam::111122223333:user/operator"] }
        Condition = { Bool = { "aws:MultiFactorAuthPresent" = "true" } }
      },
      {
        Sid       = "NamedFederatedRoles"
        Effect    = "Allow"
        Action    = "sts:AssumeRole"
        Principal = { AWS = ["arn:aws:iam::111122223333:role/aws-reserved/sso.amazonaws.com/AWSReservedSSO_Operator_0123456789abcdef"] }
      },
    ]
    error_message = "IAM users assume the teardown role only with MFA; allowlisted federated roles have their own statement (ADR-0014 item 5). Nothing else is trusted: not the CI role, not GitHub."
  }
}

run "teardown_trust_with_users_only_has_one_statement" {
  command = plan

  variables {
    teardown_trusted_principal_arns = ["arn:aws:iam::111122223333:user/operator"]
  }

  assert {
    condition     = [for s in jsondecode(aws_iam_role.teardown.assume_role_policy).Statement : s.Sid] == ["NamedUsersWithMfa"]
    error_message = "A statement with no principal is not written."
  }
}

run "teardown_trust_refuses_an_empty_list" {
  command = plan

  variables {
    teardown_trusted_principal_arns = []
  }

  expect_failures = [terraform_data.teardown_trust_is_configured]
}

run "teardown_trust_refuses_an_assumed_role_session_arn" {
  command = plan

  variables {
    teardown_trusted_principal_arns = ["arn:aws:sts::111122223333:assumed-role/Admin/operator"]
  }

  expect_failures = [var.teardown_trusted_principal_arns]
}

run "teardown_trust_refuses_the_account_root" {
  command = plan

  variables {
    teardown_trusted_principal_arns = ["arn:aws:iam::111122223333:root"]
  }

  expect_failures = [var.teardown_trusted_principal_arns]
}

run "teardown_trust_refuses_the_ci_plan_role" {
  command = plan

  variables {
    teardown_trusted_principal_arns = ["arn:aws:iam::111122223333:role/spa-platform-poc-ci-plan"]
  }

  expect_failures = [terraform_data.teardown_trust_is_configured]
}

# Applied against the mock provider, because the role's id and ARN are only known after an apply.
run "teardown_policy_is_the_roles_own_and_its_arn_is_an_output" {
  command = apply

  # Apply only the role and its policy: the state bucket intentionally prevents destruction,
  # including Terraform test cleanup. Earlier full plans still cover the complete root.
  # https://developer.hashicorp.com/terraform/language/tests#run-blocks (plan_options.target)
  plan_options {
    target = [aws_iam_role.teardown, aws_iam_role_policy.teardown]
  }

  assert {
    condition     = aws_iam_role_policy.teardown.role == aws_iam_role.teardown.id
    error_message = "The teardown policy is the teardown role's own inline policy."
  }

  assert {
    condition     = output.teardown_role_arn == aws_iam_role.teardown.arn
    error_message = "Bootstrap outputs teardown_role_arn for humans (ADR-0014 item 8)."
  }
}
