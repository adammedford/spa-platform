output "user_pool_id" {
  description = "The user pool's id. The apps root creates each app's client in it."
  value       = aws_cognito_user_pool.this.id
}

output "user_pool_arn" {
  description = "The user pool's ARN."
  value       = aws_cognito_user_pool.this.arn
}

output "issuer" {
  description = <<-EOT
    The issuer: https:// plus the pool's endpoint, cognito-idp.<region>.amazonaws.com/<pool id>.
    contracts/infra-outputs.schema.json -> issuer, and the value behind Issuers:{name}:Authority in
    contracts/platform-config.schema.json. Discovery is at <issuer>/.well-known/openid-configuration.
  EOT
  value       = "https://${aws_cognito_user_pool.this.endpoint}"
}

output "cognito_domain" {
  description = <<-EOT
    The managed-login host, no scheme: <prefix>.auth.<region>.amazoncognito.com.
    contracts/infra-outputs.schema.json -> cognitoDomain; Issuers:{name}:BrowserOrigin is https://
    plus this value.
  EOT
  value       = "${aws_cognito_user_pool_domain.this.domain}.auth.${data.aws_region.current.region}.amazoncognito.com"
}

output "resource_servers" {
  description = "Each resource server's identifier, mapped to the full scope names a client asks for (<identifier>/<scope>)."
  value       = { for id, rs in aws_cognito_resource_server.this : id => rs.scope_identifiers }
}

output "test_users" {
  description = <<-EOT
    The test users, keyed by username, each naming the parameter its password lives in and never
    the password. The shape of contracts/infra-outputs.schema.json -> testUsers.
  EOT
  value = {
    for name, p in aws_ssm_parameter.test_user_password : name => { passwordParameter = p.name }
  }
}
