# Plans the module against a mock AWS provider: no credentials, no network beyond `terraform init`.
#   terraform -chdir=infra/modules/idp-cognito init -backend=false
#   terraform -chdir=infra/modules/idp-cognito test

mock_provider "aws" {}
mock_provider "random" {}

variables {
  cell          = "poc"
  domain_prefix = "spa-platform-poc-111122223333"
  resource_servers = [
    {
      identifier = "https://orders.example.test"
      name       = "orders"
      scopes = [
        { name = "read", description = "Read orders" },
        { name = "admin", description = "Administer orders" },
      ]
    },
  ]
  users = [
    { username = "alice", email = "alice@example.test", name = "Alice Example" },
    { username = "bob", email = "bob@example.test", name = "Bob Example" },
  ]
}

run "pool_is_on_the_essentials_plan" {
  command = plan

  assert {
    condition     = aws_cognito_user_pool.this.user_pool_tier == "ESSENTIALS"
    error_message = "The user pool must be on the Essentials plan (plan task 1.1)."
  }
}

run "domain_is_a_prefix_domain_on_managed_login" {
  command = plan

  assert {
    condition     = aws_cognito_user_pool_domain.this.domain == var.domain_prefix
    error_message = "The domain must be the given prefix, not a custom domain."
  }

  assert {
    condition     = aws_cognito_user_pool_domain.this.certificate_arn == null
    error_message = "A prefix domain takes no certificate; one here would make it a custom domain."
  }

  # 2 is managed login; 1 is the classic hosted UI, which takes no branding style (task 1.8).
  assert {
    condition     = aws_cognito_user_pool_domain.this.managed_login_version == 2
    error_message = "The domain must serve managed login (version 2), not the classic hosted UI."
  }
}

run "domain_prefix_naming_aws_is_refused" {
  command = plan

  variables {
    domain_prefix = "my-cognito-login"
  }

  expect_failures = [var.domain_prefix]
}

run "one_resource_server_per_input_with_its_scopes" {
  command = plan

  assert {
    condition     = keys(aws_cognito_resource_server.this) == ["https://orders.example.test"]
    error_message = "Expected exactly one resource server, keyed by its identifier."
  }

  assert {
    condition     = aws_cognito_resource_server.this["https://orders.example.test"].name == "orders"
    error_message = "The resource server's name must come from the input."
  }

  assert {
    condition = (
      toset([for s in aws_cognito_resource_server.this["https://orders.example.test"].scope : "${s.scope_name}=${s.scope_description}"])
      == toset(["read=Read orders", "admin=Administer orders"])
    )
    error_message = "The resource server must carry exactly the input's scopes and descriptions."
  }
}

run "one_confirmed_user_per_input_with_its_attributes" {
  command = plan

  assert {
    condition     = keys(aws_cognito_user.this) == ["alice", "bob"]
    error_message = "Expected one user per input, keyed by username."
  }

  assert {
    condition = aws_cognito_user.this["alice"].attributes == tomap({
      email          = "alice@example.test"
      email_verified = "true"
      name           = "Alice Example"
    })
    error_message = "A test user carries its email (verified, so sign-in needs no code) and its name."
  }

  assert {
    condition     = aws_cognito_user.this["alice"].message_action == "SUPPRESS"
    error_message = "Creating a test user must send no invitation."
  }
}

# Applied, against the mocks, so the generated values are known and can be compared.
run "each_password_is_generated_into_a_secure_parameter" {
  command = apply

  assert {
    condition     = aws_ssm_parameter.test_user_password["alice"].name == "/spa-platform/poc/test-users/alice"
    error_message = "The password parameter lives under the cell's path (contracts/infra-outputs.example.json -> testUsers)."
  }

  assert {
    condition     = aws_ssm_parameter.test_user_password["alice"].type == "SecureString"
    error_message = "A password parameter must be a SecureString."
  }

  assert {
    condition     = nonsensitive(aws_ssm_parameter.test_user_password["alice"].value == aws_cognito_user.this["alice"].password)
    error_message = "The parameter must hold the password the user was given."
  }

  assert {
    condition     = nonsensitive(aws_cognito_user.this["alice"].password != aws_cognito_user.this["bob"].password)
    error_message = "Each user gets a password of its own."
  }
}

run "outputs_name_the_parameter_never_the_password" {
  command = apply

  assert {
    condition = output.test_users == {
      alice = { passwordParameter = "/spa-platform/poc/test-users/alice" }
      bob   = { passwordParameter = "/spa-platform/poc/test-users/bob" }
    }
    error_message = "test_users must take the shape of contracts/infra-outputs.schema.json -> testUsers."
  }

  assert {
    condition     = !strcontains(jsonencode(output.test_users), nonsensitive(aws_cognito_user.this["alice"].password))
    error_message = "No output may carry a password."
  }
}

run "issuer_and_domain_feed_the_outputs_contract" {
  command = apply

  # A state of its own: in the shared one the pool already exists, so the overrides would not apply.
  state_key = "outputs"

  override_data {
    target = data.aws_region.current
    values = {
      region = "us-east-1"
    }
  }

  override_resource {
    target = aws_cognito_user_pool.this
    values = {
      id       = "us-east-1_EXAMPLE"
      endpoint = "cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
    }
  }

  override_resource {
    target = aws_cognito_user_pool_domain.this
    values = {
      domain = "spa-platform-poc-111122223333"
    }
  }

  # Proves how the module builds the two values from the pool and the domain, not agreement with
  # a contract file: the expected strings follow from the overrides above. The forms are
  # contracts/infra-outputs.example.json's issuer and the documented <prefix>.auth.<region> host.
  assert {
    condition     = output.issuer == "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
    error_message = "The issuer is https:// plus the pool's endpoint, the value behind Issuers:{name}:Authority."
  }

  assert {
    condition     = output.cognito_domain == "spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com"
    error_message = "cognito_domain is the managed-login host, no scheme (contracts/infra-outputs.schema.json -> cognitoDomain)."
  }
}
