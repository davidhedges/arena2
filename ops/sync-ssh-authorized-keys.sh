#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TERRAFORM_DIR="$ROOT_DIR/infrastructure/hetzner-spacetimedb"

SSH_TARGET="${1:-}"

if [ -z "$SSH_TARGET" ]; then
    host="$(terraform -chdir="$TERRAFORM_DIR" output -raw ipv4_address)"
    SSH_TARGET="arena@$host"
fi

echo "Syncing Terraform-managed SSH public keys to $SSH_TARGET..."

terraform -chdir="$TERRAFORM_DIR" output -raw ssh_authorized_keys_text \
    | ssh "$SSH_TARGET" 'umask 077; mkdir -p ~/.ssh; cat > ~/.ssh/authorized_keys; chmod 700 ~/.ssh; chmod 600 ~/.ssh/authorized_keys'

echo "SSH authorized_keys synced."
