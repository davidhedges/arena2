output "server_name" {
  description = "Hetzner server name."
  value       = hcloud_server.spacetimedb.name
}

output "ipv4_address" {
  description = "Public IPv4 address."
  value       = hcloud_server.spacetimedb.ipv4_address
}

output "ipv6_address" {
  description = "Public IPv6 address."
  value       = hcloud_server.spacetimedb.ipv6_address
}

output "ssh_command" {
  description = "SSH command for the non-root admin user created by cloud-init."
  value       = "ssh arena@${hcloud_server.spacetimedb.ipv4_address}"
}

output "ssh_authorized_keys_text" {
  description = "Newline-separated SSH public keys that should be installed for the arena admin user."
  value       = join("\n", local.ssh_authorized_keys)
}

output "data_volume_name" {
  description = "Persistent Hetzner volume mounted at /stdb."
  value       = hcloud_volume.spacetimedb_data.name
}

output "data_volume_device" {
  description = "Expected Linux device path for the persistent Hetzner volume."
  value       = "/dev/disk/by-id/scsi-0HC_Volume_${hcloud_volume.spacetimedb_data.id}"
}

output "local_health_url" {
  description = "Host-local health endpoint. SSH to the server before curling this URL."
  value       = "http://127.0.0.1/healthz"
}

output "spacetimedb_http_url" {
  description = "Initial HTTP URL. Use HTTPS after DNS and certbot are configured."
  value       = trimspace(var.domain_name) != "" ? "http://${trimspace(var.domain_name)}" : "http://${hcloud_server.spacetimedb.ipv4_address}"
}

output "spacetimedb_https_url" {
  description = "Expected HTTPS URL after running ops/enable-spacetimedb-tls.sh."
  value       = trimspace(var.domain_name) != "" ? "https://${trimspace(var.domain_name)}" : null
}

output "cloudflare_dns_name" {
  description = "Cloudflare DNS record name when Cloudflare DNS is enabled."
  value       = try(cloudflare_dns_record.game_ipv4[0].name, null)
}
