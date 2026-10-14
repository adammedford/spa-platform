# The fields of contracts/infra-outputs.schema.json this root can answer so far.
# Task 1.7's seed.sh and deploy.sh assemble outputs.json from these; the ECS and
# log-group fields arrive with task 1.4.

output "region" {
  description = "The contract's `region`."
  value       = var.region
}

output "cell" {
  description = "The contract's `cell`."
  value       = var.cell
}

output "bucket" {
  description = "The contract's `bucket`: the platform's one private bucket."
  value       = module.cell.bucket
}

output "alb_dns_name" {
  description = "The contract's `albDnsName`: the internal load balancer the tunnel forwards to."
  value       = module.cell.alb_dns_name
}

output "ecr_gateway" {
  description = "The contract's `ecrGateway`."
  value       = module.cell.ecr_gateway
}

output "ecr_mock_api" {
  description = "The contract's `ecrMockApi`."
  value       = module.cell.ecr_mock_api
}

# Nothing else. contracts/infra-outputs.schema.json is additionalProperties: false,
# so an output this root publishes that the contract has no field for is a trap for
# whatever assembles outputs.json in task 1.7. The network and the security groups
# are module outputs, which is where tasks 1.3 and 1.4 read them.
