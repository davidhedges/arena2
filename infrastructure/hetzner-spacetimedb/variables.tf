variable "project_name" {
  description = "Short project name used in Hetzner resource names and labels."
  type        = string
  default     = "arena"
}

variable "environment" {
  description = "Environment label for this host."
  type        = string
  default     = "dev"
}

variable "location" {
  description = "Hetzner Cloud location. Singapore is sin."
  type        = string
  default     = "sin"
}

variable "server_type" {
  description = "Hetzner Cloud server type. cpx22 is the current small CPX dev host for Singapore; use cpx32 if SpacetimeDB needs more memory."
  type        = string
  default     = "cpx22"
}

variable "image" {
  description = "Server image."
  type        = string
  default     = "ubuntu-24.04"
}

variable "data_volume_size_gb" {
  description = "Size of the persistent SpacetimeDB data volume in GB."
  type        = number
  default     = 20
}

variable "data_volume_delete_protection" {
  description = "Protect the SpacetimeDB data volume from accidental deletion."
  type        = bool
  default     = true
}

variable "ssh_public_key_path" {
  description = "Primary local SSH public key to register with Hetzner and install for the arena admin user."
  type        = string
  default     = "~/.ssh/id_ed25519.pub"
}

variable "ssh_public_key_paths" {
  description = "Additional local SSH public key paths to register with Hetzner and install for the arena admin user."
  type        = list(string)
  default     = []
}

variable "ssh_public_keys" {
  description = "Additional SSH public key strings to register with Hetzner and install for the arena admin user."
  type        = list(string)
  default     = []
}

variable "ssh_allowed_cidrs_ipv4" {
  description = "IPv4 CIDRs allowed to SSH to the host. Replace the default with your current /32 before production use."
  type        = list(string)
  default     = ["0.0.0.0/0"]
}

variable "ssh_allowed_cidrs_ipv6" {
  description = "IPv6 CIDRs allowed to SSH to the host. Replace the default with your current /128 before production use."
  type        = list(string)
  default     = []
}

variable "domain_name" {
  description = "Optional DNS name for the SpacetimeDB host. TLS is enabled later by ops/enable-spacetimedb-tls.sh after DNS points at the server."
  type        = string
  default     = ""
}

variable "cloudflare_zone_id" {
  description = "Optional Cloudflare zone ID. Prefer cloudflare_zone_name unless you have copied the opaque zone ID from Cloudflare."
  type        = string
  default     = ""
}

variable "cloudflare_zone_name" {
  description = "Optional Cloudflare root zone name, for example meandmyson.org. When set with domain_name, Terraform looks up the zone ID and creates DNS records."
  type        = string
  default     = ""
}

variable "cloudflare_record_name" {
  description = "Cloudflare DNS record name. Leave empty to use domain_name."
  type        = string
  default     = ""
}

variable "cloudflare_proxied" {
  description = "Whether Cloudflare should proxy the game server DNS record. Keep false for the first real-time websocket deployment."
  type        = bool
  default     = false
}

variable "cloudflare_manage_tls_settings" {
  description = "Whether to manage Cloudflare zone TLS-related settings with cloudflare_zone_setting resources."
  type        = bool
  default     = false
}
