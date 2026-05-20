#!/usr/bin/env bash
set -euo pipefail

DOMAIN="${1:-}"
SSH_TARGET="${2:-}"
LETSENCRYPT_EMAIL="${LETSENCRYPT_EMAIL:-}"

if [ -z "$DOMAIN" ] || [ -z "$SSH_TARGET" ] || [ -z "$LETSENCRYPT_EMAIL" ]; then
    echo "Usage:"
    echo "  LETSENCRYPT_EMAIL=you@example.com $0 arena.example.com arena@203.0.113.10"
    exit 2
fi

ssh "$SSH_TARGET" "DOMAIN='$DOMAIN' LETSENCRYPT_EMAIL='$LETSENCRYPT_EMAIL' bash -s" <<'REMOTE'
set -euo pipefail

sudo env DOMAIN="$DOMAIN" perl -0pi -e 's/server_name\s+[^;]+;/server_name $ENV{DOMAIN};/' /etc/nginx/sites-available/spacetimedb
sudo nginx -t
sudo systemctl reload nginx
sudo certbot --nginx \
    --non-interactive \
    --agree-tos \
    --redirect \
    --email "$LETSENCRYPT_EMAIL" \
    -d "$DOMAIN"
sudo systemctl reload nginx
REMOTE

echo "TLS enabled for $DOMAIN."
