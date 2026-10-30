#!/usr/bin/env bash
set -euo pipefail

# Trust the VPS's public TLS certificate in the host CA store and map its SDK
# hostname to the directly reachable VPS. No SSH connection or Proton certutil.
VPS_HOST="${MIKUSB_VPS_HOST:-192.227.172.144}"
VPS_IP="${MIKUSB_VPS_IP:-192.227.172.144}"
die() { printf 'setup-cachyos: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"; }

[[ "$EUID" -ne 0 ]] || die 'Run this script as your normal Steam user, not root.'
need sudo
need python3
need openssl
need getent

# Let users select the stable IP or their current DDNS A record.
if [[ "$VPS_HOST" != *[!0-9.]* ]]; then
  VPS_IP="$VPS_HOST"
else
  VPS_IP=$(getent ahostsv4 "$VPS_HOST" | awk 'NR==1 {print $1}')
  [[ -n "$VPS_IP" ]] || die "Could not resolve VPS host: $VPS_HOST"
fi

STATE="$HOME/.local/share/mikusb-vps"
mkdir -p "$STATE"
chmod 700 "$STATE"
CERT="$STATE/wildcard.xoyo.games.pem"
cat > "$CERT" <<'MIKUSB_CERT'
-----BEGIN CERTIFICATE-----
MIIDUjCCAjqgAwIBAgIUVHG72CWGXIVC7yy2Nn27NBHmpfUwDQYJKoZIhvcNAQEL
BQAwFzEVMBMGA1UEAwwMKi54b3lvLmdhbWVzMB4XDTI2MTAyOTIzNDcxNFoXDTM2
MTAyNjIzNDcxNFowFzEVMBMGA1UEAwwMKi54b3lvLmdhbWVzMIIBIjANBgkqhkiG
9w0BAQEFAAOCAQ8AMIIBCgKCAQEA8Nebx+VQfO/q8CprY09vGMsbMX2PERbpqajf
fEeLapw6/uP/qwWknkqhVc4BjqKn3QnvUmMgPfsypI1ZJA1tOljvnZRPHWs2YEiY
ZVgJD90N95jFMLfPMbpj66cJ2sgJX04pfliKYIz1gL+Lm7/T5AlcF6AQmraVlQZE
vS6z7oQdK6jOb5Lfe1pttKeRnsDv62jCjLWC0wCpX6XVIslBweiOFdjZvxagrN5y
OeCkM7L/KN1lvr/IRVxZCSUhZ5dFrzsGppbtrdyMWGWJPU8R0sGz95RGxHEde5Py
bVKFSztj24HtIkR1uzd/ZSYxT9raeQnCCaWna6i/duCH/jPfNwIDAQABo4GVMIGS
MB0GA1UdDgQWBBQqiJoy3/ugDlzCSc1AfhyyGkeN0jAfBgNVHSMEGDAWgBQqiJoy
3/ugDlzCSc1AfhyyGkeN0jASBgNVHRMBAf8ECDAGAQH/AgEAMA4GA1UdDwEB/wQE
AwIBpjATBgNVHSUEDDAKBggrBgEFBQcDATAXBgNVHREEEDAOggwqLnhveW8uZ2Ft
ZXMwDQYJKoZIhvcNAQELBQADggEBAEHWEqbVkRGCOF0CdueTVxDXgG9I8pAzH4wa
fsJA5FJmAK+WWhWzFkJSLaGpXY18DUO3yr6mYtwf/4knx/LlRvErQFYGOdFJDY5O
mFFW62gT1ISK2F2Gt5SYqbNzCs/lyFR0kmLnPInaI0xnSmgBqYRJyQef9LmZjd1/
BL0kZiyx2hkHaB21cb3C0Zp9Av0rYDHN/Bhj4jbY00hNKojq5SOnsO7a/s0Irc+U
zLQ8Mkwoyhdw8MgutkGIvKg3VB1/SS6XgLJbe4MBrEZvuTbM1vKATAoRgXfYEE/w
v3Y5ipRpHW/NuEiAqHiuOhc51RJ0r+f9bldV+tw3FytmC3fyGW4=
-----END CERTIFICATE-----
MIKUSB_CERT
chmod 644 "$CERT"
openssl x509 -in "$CERT" -noout -subject -fingerprint -sha256

# Steam Deck ships with its root filesystem read-only. Temporarily make it
# writable during the trust/hosts update, then restore its previous mode.
if command -v steamos-readonly >/dev/null 2>&1 && steamos-readonly status | grep -qi enabled; then
  sudo steamos-readonly disable
  trap 'sudo steamos-readonly enable' EXIT
fi

printf 'Adding the bundled public certificate to the host trust store...\n'
sudo install -Dm644 "$CERT" /etc/ca-certificates/trust-source/anchors/mikusb-vps.crt
sudo update-ca-trust extract

HOSTS_MARKER='# MikuSB VPS SDK routing (managed by setup-cachyos.sh)'
sudo sed -i "\\|$HOSTS_MARKER|d" /etc/hosts
printf '%s xgsdk.xoyo.games %s\n' "$VPS_IP" "$HOSTS_MARKER" | sudo tee -a /etc/hosts >/dev/null

printf '\nThe VPS is reached directly over the internet; no SSH tunnel or proxy setting is needed.\n'
printf 'Snowbreak SDK hostname: xgsdk.xoyo.games -> %s\n' "$VPS_IP"
printf 'Testing certificate-verified SDK access...\n'
curl --fail --silent --show-error --noproxy '*' \
  https://xgsdk.xoyo.games:13443/getGameConfig
printf '\n\nIf this succeeds, start Snowbreak normally from Steam.\n'
