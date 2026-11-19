# Named for contracts/infra-outputs.schema.json, which task 1.7 assembles into
# infra/envs/poc/outputs.json after every apply. No output carries a secret.

output "clients" {
  description = <<-EOT
    contracts/infra-outputs.schema.json -> clients: per appId, the client id, the parameter that
    holds its secret and that parameter's version, and for an app with fragments the same three
    per fragment under fragments.{fragmentId}. Never the secret. spactl app add reads it, and
    the platform root reads the client ids of apps 1 and 2 for the mock API's allowlists.
  EOT
  # An app with fragments gains fragments.{fragmentId}, the same three fields per fragment client,
  # mirroring the registry's nesting; an app with none keeps the three fields alone. The optional key
  # comes from a for over a list of zero or one, since a conditional's two objects would differ in
  # type.
  value = {
    for id, c in aws_cognito_user_pool_client.app : id => merge(
      {
        clientId        = c.id
        secretParameter = aws_ssm_parameter.client_secret[id].name
        secretVersion   = aws_ssm_parameter.client_secret[id].version
      },
      {
        for fragments in [{
          for key, f in local.fragments : f.fragment_id => {
            clientId        = aws_cognito_user_pool_client.fragment[key].id
            secretParameter = aws_ssm_parameter.fragment_client_secret[key].name
            secretVersion   = aws_ssm_parameter.fragment_client_secret[key].version
          } if f.app_id == id
        }] : "fragments" => fragments if length(fragments) > 0
      },
    )
  }
}
