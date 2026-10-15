# What the platform root publishes, and what task 1.4 builds on.
#
# The names follow contracts/infra-outputs.schema.json where that contract has a
# field for them. The rest are the module's own seams: the service of task 1.4 takes
# the network, the repositories and the target group from here.

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

output "alb_dns_name" {
  description = "The internal load balancer's DNS name, as the contract's `albDnsName`. The tunnel forwards to it."
  value       = aws_lb.cell.dns_name
}

output "target_group_arn" {
  description = "The one target group, which task 1.4's service registers its task in."
  value       = aws_lb_target_group.gateway.arn
}

output "https_listener_arn" {
  description = "The one HTTPS listener. Task 1.4's service depends on it, so the target group is attached to the load balancer before the service registers a task."
  value       = aws_lb_listener.https.arn
}

# --- Task 1.4a ---------------------------------------------------------------------------------

output "ecs_cluster" {
  description = "The cell's cluster, as the contract's `ecsCluster`. tunnel.sh needs it to build the Session Manager target."
  value       = aws_ecs_cluster.cell.name
}

output "ecs_service" {
  description = "The one service, as the contract's `ecsService`. deploy.sh waits on it for steady state."
  value       = aws_ecs_service.gateway.name
}

output "log_group" {
  description = "The task's log group, as the contract's `logGroup`. The deployed tests read a run's gateway log from it."
  value       = aws_cloudwatch_log_group.gateway.name
}

output "gateway_image" {
  description = <<-EOT
    The gateway image this apply placed, as the contract's `gatewayImage`. It is the input echoed
    back, which is the point: deploy.sh asserts the running digest against what it applied.
  EOT
  value       = var.gateway_image
}
