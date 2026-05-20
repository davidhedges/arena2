locals {
  name_prefix            = "${var.project_name}-${var.environment}"
  primary_ssh_public_key = chomp(file(pathexpand(var.ssh_public_key_path)))
  ssh_public_keys_from_paths = [
    for path in var.ssh_public_key_paths : chomp(file(pathexpand(path)))
  ]
  ssh_authorized_keys = distinct(compact(concat(
    [local.primary_ssh_public_key],
    local.ssh_public_keys_from_paths,
    [for key in var.ssh_public_keys : trimspace(key)]
  )))
  additional_ssh_public_keys = slice(local.ssh_authorized_keys, 1, length(local.ssh_authorized_keys))
  additional_ssh_public_key_map = {
    for index, key in local.additional_ssh_public_keys : tostring(index + 1) => key
  }
  data_volume_name       = "${local.name_prefix}-spacetimedb-data"
  nginx_server_name      = trimspace(var.domain_name) != "" ? trimspace(var.domain_name) : "_"
  cloudflare_zone_id     = can(regex("^[0-9a-fA-F]{32}$", trimspace(var.cloudflare_zone_id))) ? trimspace(var.cloudflare_zone_id) : ""
  cloudflare_zone_name   = trimspace(var.cloudflare_zone_name) != "" ? trimspace(var.cloudflare_zone_name) : (local.cloudflare_zone_id == "" ? trimspace(var.cloudflare_zone_id) : "")
  cloudflare_dns_enabled = (local.cloudflare_zone_id != "" || local.cloudflare_zone_name != "") && trimspace(var.domain_name) != ""
  cloudflare_zone_id_effective = local.cloudflare_zone_id != "" ? local.cloudflare_zone_id : (
    local.cloudflare_dns_enabled ? data.cloudflare_zone.selected[0].id : ""
  )
  cloudflare_record_name = trimspace(var.cloudflare_record_name) != "" ? trimspace(var.cloudflare_record_name) : trimspace(var.domain_name)
  ssh_allowed_cidrs      = concat(var.ssh_allowed_cidrs_ipv4, var.ssh_allowed_cidrs_ipv6)
  public_web_cidrs       = ["0.0.0.0/0", "::/0"]
  common_labels = {
    project     = var.project_name
    environment = var.environment
    service     = "spacetimedb"
    managed_by  = "terraform"
  }
}

data "cloudflare_zone" "selected" {
  count = local.cloudflare_dns_enabled && local.cloudflare_zone_id == "" ? 1 : 0

  filter = {
    name = local.cloudflare_zone_name
  }
}

resource "hcloud_volume" "spacetimedb_data" {
  name              = local.data_volume_name
  size              = var.data_volume_size_gb
  location          = var.location
  format            = "ext4"
  delete_protection = var.data_volume_delete_protection
  labels            = local.common_labels
}

resource "hcloud_ssh_key" "operator" {
  name       = "${local.name_prefix}-operator"
  public_key = local.primary_ssh_public_key
  labels     = local.common_labels
}

resource "hcloud_ssh_key" "additional_operators" {
  for_each = local.additional_ssh_public_key_map

  name       = "${local.name_prefix}-operator-${each.key}"
  public_key = each.value
  labels     = local.common_labels
}

resource "hcloud_firewall" "spacetimedb" {
  name   = "${local.name_prefix}-spacetimedb"
  labels = local.common_labels

  rule {
    description = "ICMP diagnostics"
    direction   = "in"
    protocol    = "icmp"
    source_ips  = local.public_web_cidrs
  }

  dynamic "rule" {
    for_each = length(local.ssh_allowed_cidrs) > 0 ? [1] : []
    content {
      description = "SSH admin access"
      direction   = "in"
      protocol    = "tcp"
      port        = "22"
      source_ips  = local.ssh_allowed_cidrs
    }
  }

  rule {
    description = "HTTP for health checks and Let's Encrypt challenge"
    direction   = "in"
    protocol    = "tcp"
    port        = "80"
    source_ips  = local.public_web_cidrs
  }

  rule {
    description = "HTTPS client traffic"
    direction   = "in"
    protocol    = "tcp"
    port        = "443"
    source_ips  = local.public_web_cidrs
  }
}

resource "hcloud_server" "spacetimedb" {
  name        = "${local.name_prefix}-spacetimedb-01"
  image       = var.image
  server_type = var.server_type
  location    = var.location
  labels      = local.common_labels

  ssh_keys     = concat([hcloud_ssh_key.operator.id], [for key in hcloud_ssh_key.additional_operators : key.id])
  firewall_ids = [hcloud_firewall.spacetimedb.id]

  public_net {
    ipv4_enabled = true
    ipv6_enabled = true
  }

  user_data = templatefile("${path.module}/cloud-init.yaml.tftpl", {
    data_volume_id      = hcloud_volume.spacetimedb_data.id
    data_volume_name    = local.data_volume_name
    nginx_server_name   = local.nginx_server_name
    ssh_authorized_keys = local.ssh_authorized_keys
  })

  lifecycle {
    # Cloud-init only runs on first boot. Do not replace a live game server just
    # because bootstrap inputs changed after provisioning.
    ignore_changes = [ssh_keys, user_data]
  }
}

resource "hcloud_volume_attachment" "spacetimedb_data" {
  volume_id = hcloud_volume.spacetimedb_data.id
  server_id = hcloud_server.spacetimedb.id
  automount = false
}

resource "cloudflare_dns_record" "game_ipv4" {
  count = local.cloudflare_dns_enabled ? 1 : 0

  zone_id = local.cloudflare_zone_id_effective
  name    = local.cloudflare_record_name
  type    = "A"
  content = hcloud_server.spacetimedb.ipv4_address
  ttl     = 1
  proxied = var.cloudflare_proxied
  comment = "Arena SpacetimeDB game server IPv4"
}

resource "cloudflare_dns_record" "game_ipv6" {
  count = local.cloudflare_dns_enabled ? 1 : 0

  zone_id = local.cloudflare_zone_id_effective
  name    = local.cloudflare_record_name
  type    = "AAAA"
  content = hcloud_server.spacetimedb.ipv6_address
  ttl     = 1
  proxied = var.cloudflare_proxied
  comment = "Arena SpacetimeDB game server IPv6"
}

resource "cloudflare_zone_setting" "ssl" {
  count = local.cloudflare_dns_enabled && var.cloudflare_manage_tls_settings ? 1 : 0

  zone_id    = local.cloudflare_zone_id_effective
  setting_id = "ssl"
  value      = "strict"
}

resource "cloudflare_zone_setting" "always_use_https" {
  count = local.cloudflare_dns_enabled && var.cloudflare_manage_tls_settings ? 1 : 0

  zone_id    = local.cloudflare_zone_id_effective
  setting_id = "always_use_https"
  value      = "on"
}

resource "cloudflare_zone_setting" "automatic_https_rewrites" {
  count = local.cloudflare_dns_enabled && var.cloudflare_manage_tls_settings ? 1 : 0

  zone_id    = local.cloudflare_zone_id_effective
  setting_id = "automatic_https_rewrites"
  value      = "on"
}

resource "cloudflare_zone_setting" "websockets" {
  count = local.cloudflare_dns_enabled && var.cloudflare_manage_tls_settings ? 1 : 0

  zone_id    = local.cloudflare_zone_id_effective
  setting_id = "websockets"
  value      = "on"
}
