#!/usr/bin/env bash
set -euo pipefail

SSH_TARGET="${1:-}"

if [ -z "$SSH_TARGET" ]; then
    echo "Usage: $0 arena@203.0.113.10"
    exit 2
fi

ssh "$SSH_TARGET" <<'REMOTE'
set -euo pipefail

echo "== systemd =="
sudo systemctl --no-pager --full status spacetimedb nginx || true

echo
echo "== nginx health =="
DOMAIN="$(sudo awk '/server_name/ { gsub(";", "", $2); print $2; exit }' /etc/nginx/sites-available/spacetimedb)"
if [ -n "$DOMAIN" ] && [ "$DOMAIN" != "_" ] && sudo test -d "/etc/letsencrypt/live/$DOMAIN"; then
    curl --resolve "$DOMAIN:443:127.0.0.1" -fsS "https://$DOMAIN/healthz"
else
    curl -fsS http://127.0.0.1/healthz
fi
echo

echo
echo "== spacetimedb logs =="
sudo journalctl -u spacetimedb --no-pager -n 100
REMOTE
