# Named for contracts/infra-outputs.schema.json, which task 1.7 assembles into
# infra/envs/poc/outputs.json after every apply. No output here carries a password.

output "region" {
  description = "contracts/infra-outputs.schema.json -> region."
  value       = var.region
}

output "cell" {
  description = "contracts/infra-outputs.schema.json -> cell."
  value       = var.cell
}

output "issuer" {
  description = "contracts/infra-outputs.schema.json -> issuer, and Issuers:{name}:Authority in platform configuration."
  value       = module.idp.issuer
}

output "cognito_domain" {
  description = "contracts/infra-outputs.schema.json -> cognitoDomain. Issuers:{name}:BrowserOrigin is https:// plus this value."
  value       = module.idp.cognito_domain
}

output "cognito_domain_prefix" {
  description = "The managed-login prefix alone."
  value       = local.domain_prefix
}

output "test_users" {
  description = "contracts/infra-outputs.schema.json -> testUsers: each user's password parameter, never the password."
  value       = module.idp.test_users
}

output "user_pool_id" {
  description = "The user pool the apps root (task 1.8) creates each app's client in."
  value       = module.idp.user_pool_id
}

output "user_pool_arn" {
  description = "The user pool's ARN."
  value       = module.idp.user_pool_arn
}

output "resource_servers" {
  description = "Each resource server's identifier, mapped to the full scope names a client may ask for. The apps root checks an app's scopes against these."
  value       = module.idp.resource_servers
}
