terraform {
  required_version = ">= 1.6.0"

  required_providers {
    hcloud = {
      source  = "hetznercloud/hcloud"
      version = "~> 1.62"
    }

    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.19"
    }
  }
}

# Uses HCLOUD_TOKEN from the environment.
provider "hcloud" {}

# Uses CLOUDFLARE_API_TOKEN from the environment when Cloudflare resources are enabled.
provider "cloudflare" {}
