# Hetzner SpacetimeDB Host

This provisions the first self-hosted SpacetimeDB target for the game:

- One Ubuntu 24.04 Hetzner Cloud VM in Singapore (`sin`).
- One persistent Hetzner Cloud volume mounted at `/stdb` for SpacetimeDB state.
- Hetzner firewall allowing SSH, HTTP, and HTTPS only.
- SpacetimeDB installed under `/stdb` and run as a dedicated `spacetimedb` user.
- Nginx reverse proxy on port 80, exposing only `/v1/identity` and `/v1/database/<db>/subscribe`.
- SpacetimeDB listens only on `127.0.0.1:3000`.
- Optional Cloudflare DNS records for the game host.

Publishing is intentionally not exposed over the public reverse proxy. Publish over SSH with the host-local SpacetimeDB endpoint.

## Provision

Install Terraform or OpenTofu, then:

```bash
cd infrastructure/hetzner-spacetimedb
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars`. At minimum, set `ssh_allowed_cidrs_ipv4` to the devs' current public IPs as `/32` entries if possible:

```hcl
ssh_allowed_cidrs_ipv4 = [
  "203.0.113.10/32",
  "203.0.113.11/32",
]
ssh_allowed_cidrs_ipv6 = []
```

Terraform does not load `.env` files automatically. Copy `.env.example` to `.env`, add `HCLOUD_TOKEN` and `CLOUDFLARE_API_TOKEN`, then load them into your shell before running Terraform:

```bash
set -a
source ../../.env
set +a
```

You also need SSH public keys for everyone who should be able to deploy or log in as `arena`. The primary key path is configured by `ssh_public_key_path`, and extra keys can be added with `ssh_public_key_paths` or `ssh_public_keys`.

```hcl
ssh_public_key_path = "~/.ssh/id_ed25519.pub"
ssh_public_key_paths = [
  "~/.ssh/arena_github_deploy.pub",
]
ssh_public_keys = [
  "ssh-ed25519 AAAAC3... dev@example.com",
]
```

If your primary key file does not exist, generate one with `ssh-keygen -t ed25519 -C "arena-hetzner"`.

For Cloudflare DNS records, set these in `terraform.tfvars`:

```bash
domain_name = "arena.example.com"
cloudflare_zone_name = "example.com"
cloudflare_record_name = "arena.example.com"
cloudflare_proxied = false
```

`cloudflare_zone_name` is the root zone in Cloudflare, not the game subdomain. You can also set `cloudflare_zone_id` to the opaque Cloudflare Zone ID if you prefer, but do not put `example.com` in `cloudflare_zone_id`.

The Cloudflare token needs DNS edit access for the zone. `cloudflare_manage_tls_settings` is off by default because zone settings are account-wide-ish state and can conflict with manual Cloudflare configuration; enable it only if you want Terraform to own `ssl = strict`, HTTPS rewrites, Always Use HTTPS, and WebSockets for the zone.

```bash
export HCLOUD_TOKEN=your_hetzner_cloud_api_token
terraform init
terraform plan
terraform apply
```

OpenTofu works with the same files:

```bash
tofu init
tofu plan
tofu apply
```

## Verify

```bash
terraform output
ops/spacetimedb-status.sh arena@$(terraform output -raw ipv4_address)
ssh arena@$(terraform output -raw ipv4_address)
```

On the host:

```bash
findmnt /stdb
sudo systemctl status spacetimedb nginx
sudo journalctl -u spacetimedb --no-pager -n 100
```

## Publish The Game Module

From the repo root:

```bash
ARENA_HOST=$(terraform -chdir=infrastructure/hetzner-spacetimedb output -raw ipv4_address) \
  ops/deploy-spacetimedb.sh
```

This builds the Rust module, copies the WASM to the server, and runs `spacetime publish -s local arena` on the host.

## GitHub Actions Deploy

The repo includes a manual deploy workflow at `.github/workflows/spacetimedb-deploy.yml`. Start with manual deploys from the Actions tab; add automatic deploy-on-push later once the dev server path is stable.

Set these GitHub repository variables:

```text
ARENA_HOST=arena.example.com
ARENA_SSH_USER=arena
ARENA_DATABASE=arena
```

Set this GitHub repository secret:

```text
ARENA_SSH_PRIVATE_KEY=<private half of a deploy-only SSH key>
```

Recommended: use a deploy-specific key, not your personal laptop key. Generate one locally:

```bash
ssh-keygen -t ed25519 -C "github-actions-arena-deploy" -f ~/.ssh/arena_github_deploy
```

Install the public half for the `arena` user on the server:

```bash
cat ~/.ssh/arena_github_deploy.pub | ssh arena@203.0.113.10 'umask 077; mkdir -p ~/.ssh; cat >> ~/.ssh/authorized_keys'
```

Then put the private half from `~/.ssh/arena_github_deploy` into the `ARENA_SSH_PRIVATE_KEY` GitHub secret. Optionally set `ARENA_SSH_KNOWN_HOSTS` to the output of `ssh-keyscan -H arena.example.com`; otherwise the workflow will run `ssh-keyscan` during deploy.

Important: the Hetzner firewall must allow SSH from the GitHub Actions runner. The current dev-safe default is to restrict SSH to specific developer IPs, which means GitHub-hosted runners will time out. For a first dev deployment, either temporarily allow SSH from `0.0.0.0/0` with key-only auth, use a self-hosted runner with a stable IP, or add an automated firewall update step later.

## Sync Operator SSH Keys

For new servers, cloud-init installs the configured SSH public keys on first boot. Existing servers do not re-run cloud-init, so after changing `ssh_public_key_paths` or `ssh_public_keys`, run Terraform and then sync the rendered key list:

```bash
terraform -chdir=infrastructure/hetzner-spacetimedb apply
ops/sync-ssh-authorized-keys.sh
```

This replaces `/home/arena/.ssh/authorized_keys` with the Terraform-managed public key list. Make sure the GitHub deploy public key and every developer public key are in Terraform before syncing.

## TLS

After DNS points at the Hetzner server, either from the Cloudflare resources above or a manual DNS record:

```bash
export LETSENCRYPT_EMAIL=you@example.com
ops/enable-spacetimedb-tls.sh arena.example.com arena@203.0.113.10
```

Then configure the Unity client to use the HTTPS/WSS endpoint instead of `ws://localhost:3000`.

## Notes

- Do not expose port `3000` publicly.
- `/stdb` is on a Hetzner volume with delete protection enabled by default. That protects Terraform destroy accidents but does not replace backups.
- The default size is intentionally small and Singapore-compatible: `cpx22` plus a 20GB data volume for first dev hosting.
- Treat this as a single-node starter. Before real users, add backups/snapshots, log retention, metrics, and a restore drill.
- The Unity client currently still has a localhost default in `NetworkManager`; that needs a build-time or asset-based environment config before shipping external builds.
