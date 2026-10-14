# What the platform root publishes, and what tasks 1.3 and 1.4 build on.
#
# The names follow contracts/infra-outputs.schema.json where that contract has a
# field for them. The rest are the module's own seams: the load balancer of task 1.3
# and the service of task 1.4 take the network and the repositories from here.

output "vpc_id" {
  description = "The cell's VPC."
  value       = aws_vpc.cell.id
}

output "public_subnet_ids" {
  description = "The two public subnets, one per zone. The load balancer needs both."
  value       = [for subnet in aws_subnet.public : subnet.id]
}

output "alb_security_group_id" {
  description = "The load balancer's group: accepts 443 from the task group only."
  value       = aws_security_group.alb.id
}

output "task_security_group_id" {
  description = "The task's group: accepts the gateway's port from the load balancer only."
  value       = aws_security_group.task.id
}

output "bucket" {
  description = "The platform bucket's name, as the infrastructure outputs contract's `bucket`."
  value       = aws_s3_bucket.platform.id
}

output "bucket_arn" {
  description = "The platform bucket, for the task role of task 1.4."
  value       = aws_s3_bucket.platform.arn
}

output "ecr_gateway" {
  description = "The gateway repository's URL, as the contract's `ecrGateway`."
  value       = aws_ecr_repository.repository["gateway"].repository_url
}

output "ecr_mock_api" {
  description = "The mock API repository's URL, as the contract's `ecrMockApi`."
  value       = aws_ecr_repository.repository["mock-api"].repository_url
}
