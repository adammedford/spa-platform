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

# Task 1.4a's four.

output "ecs_cluster" {
  description = "The contract's `ecsCluster`. tunnel.sh builds the Session Manager target from it."
  value       = module.cell.ecs_cluster
}

output "ecs_service" {
  description = "The contract's `ecsService`. deploy.sh waits on it for steady state."
  value       = module.cell.ecs_service
}

output "log_group" {
  description = "The contract's `logGroup`. The deployed tests read a run's gateway log from it."
  value       = module.cell.log_group
}

output "gateway_image" {
  description = "The contract's `gatewayImage`: the image this apply placed, which deploy.sh asserts against the running one."
  value       = module.cell.gateway_image
}

# Nothing else. contracts/infra-outputs.schema.json is additionalProperties: false,
# so an output this root publishes that the contract has no field for is a trap for
# whatever assembles outputs.json in task 1.7. The network and the security groups
# are module outputs, which is where tasks 1.3 and 1.4 read them.
