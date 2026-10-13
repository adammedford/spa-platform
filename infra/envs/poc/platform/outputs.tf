# The fields of contracts/infra-outputs.schema.json this root can answer so far.
# Task 1.7's seed.sh and deploy.sh assemble outputs.json from these; the ALB, ECS
# and log-group fields arrive with tasks 1.3 and 1.4.

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

output "ecr_gateway" {
  description = "The contract's `ecrGateway`."
  value       = module.cell.ecr_gateway
}

output "ecr_mock_api" {
  description = "The contract's `ecrMockApi`."
  value       = module.cell.ecr_mock_api
}

output "vpc_id" {
  description = "The cell's VPC, for the tunnel and for anyone reading the plan."
  value       = module.cell.vpc_id
}

output "public_subnet_ids" {
  description = "The two public subnets the load balancer and the service use."
  value       = module.cell.public_subnet_ids
}

output "alb_security_group_id" {
  description = "The load balancer's security group."
  value       = module.cell.alb_security_group_id
}

output "task_security_group_id" {
  description = "The task's security group."
  value       = module.cell.task_security_group_id
}
