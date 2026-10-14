# Named for contracts/infra-outputs.schema.json, which task 1.7 assembles into
# infra/envs/poc/outputs.json after every apply. No output carries a secret.

output "clients" {
  description = <<-EOT
    contracts/infra-outputs.schema.json -> clients: per appId, the client id, the parameter that
    holds its secret and that parameter's version. Never the secret. spactl app add reads it, and
    the platform root reads the client ids of apps 1 and 2 for the mock API's allowlists.
  EOT
  value = {
    for id, c in aws_cognito_user_pool_client.app : id => {
      clientId        = c.id
      secretParameter = aws_ssm_parameter.client_secret[id].name
      secretVersion   = aws_ssm_parameter.client_secret[id].version
    }
  }
}
