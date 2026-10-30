#!/usr/bin/env bash
set -euo pipefail

# Trust the VPS's public TLS certificate in the host CA store and map its SDK
# hostname to the directly reachable VPS. No SSH connection or Proton certutil.
VPS_HOST="${MIKUSB_VPS_HOST:-${MIKUSB_VPS_IP:-192.227.172.144}}"
GAME_TARGET_IP="${MIKUSB_GAME_TARGET_IP:-49.51.66.126}"
die() { printf 'setup-cachyos: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"; }

[[ "$EUID" -ne 0 ]] || die 'Run this script as your normal Steam user, not root.'
need sudo
need python3
need openssl
need getent
need nft

USER_UID=$(id -u)
ROUTE_UNIT="mikusb-game-route-${USER_UID}.service"
ROUTE_HELPER=/etc/mikusb-vps/game-route.sh
ROUTE_RULES="/etc/mikusb-vps/game-route-${USER_UID}.nft"

# SteamOS may require a writable root for the service and certificate files.
if command -v steamos-readonly >/dev/null 2>&1 && steamos-readonly status | grep -qi enabled; then
  sudo steamos-readonly disable
  trap 'sudo steamos-readonly enable' EXIT
fi

if [[ "${1:-}" == --remove-game-route ]]; then
  [[ $# -eq 1 ]] || die 'Usage: setup-cachyos.sh [--remove-game-route]'
  if [[ -d /run/systemd/system ]] && command -v systemctl >/dev/null 2>&1 && [[ -f "/etc/systemd/system/$ROUTE_UNIT" ]]; then
    sudo systemctl disable --now "$ROUTE_UNIT"
  fi
  if [[ -f "$ROUTE_HELPER" ]]; then
    sudo bash "$ROUTE_HELPER" remove "$USER_UID"
  fi
  sudo rm -f -- "$ROUTE_RULES" "/etc/systemd/system/$ROUTE_UNIT"
  if [[ -d /run/systemd/system ]] && command -v systemctl >/dev/null 2>&1; then
    sudo systemctl daemon-reload
  fi
  printf 'Removed the MikuSB game redirect for UID %s. SDK trust and hostname settings remain installed.\n' "$USER_UID"
  exit 0
fi
[[ $# -eq 0 ]] || die 'Usage: setup-cachyos.sh [--remove-game-route]'

# Let users select the stable IP or their current DDNS A record.
if [[ "$VPS_HOST" != *[!0-9.]* ]]; then
  VPS_IP="$VPS_HOST"
else
  VPS_IP=$(getent ahostsv4 "$VPS_HOST" | awk 'NR==1 {print $1}')
  [[ -n "$VPS_IP" ]] || die "Could not resolve VPS host: $VPS_HOST"
fi
python3 -c 'import ipaddress, sys; [ipaddress.IPv4Address(x) for x in sys.argv[1:]]' "$VPS_IP" "$GAME_TARGET_IP" \
  || die 'VPS and game target addresses must be valid IPv4 addresses.'
[[ "$VPS_IP" != "$GAME_TARGET_IP" ]] || die 'VPS and game target addresses must be different.'

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

# Game build 3.6.0.186 was observed connecting to this IPv4 address directly.
# Redirect only this user's TCP traffic to that address and port. An nft table
# dedicated to this UID avoids changing other firewall rules.
sudo install -d -m 755 /etc/mikusb-vps
sudo tee "$ROUTE_HELPER" >/dev/null <<'MIKUSB_ROUTE_HELPER'
#!/usr/bin/env bash
set -euo pipefail
PATH=/usr/sbin:/usr/bin:/sbin:/bin
action="${1:-}"
uid="${2:-}"
[[ "$uid" =~ ^[0-9]+$ ]] || { echo 'Invalid UID' >&2; exit 1; }
table="mikusb_vps_game_${uid}"
rules="/etc/mikusb-vps/game-route-${uid}.nft"
marker='mikusb-vps-game-v1'
exists=0
if nft list table ip "$table" >/dev/null 2>&1; then
  nft list table ip "$table" | grep -Fq "$marker" || {
    echo "Refusing to alter unrecognized nft table $table" >&2; exit 1;
  }
  exists=1
fi
case "$action" in
  apply)
    [[ -f "$rules" ]] || { echo "Missing rules: $rules" >&2; exit 1; }
    { if [[ "$exists" == 1 ]]; then printf 'delete table ip %s\n' "$table"; fi; cat "$rules"; } | nft -f -
    ;;
  remove)
    if [[ "$exists" == 1 ]]; then nft delete table ip "$table"; fi
    ;;
  *) echo 'Usage: game-route.sh {apply|remove} UID' >&2; exit 1 ;;
esac
MIKUSB_ROUTE_HELPER
sudo chmod 755 "$ROUTE_HELPER"
sudo tee "$ROUTE_RULES" >/dev/null <<MIKUSB_NFT
table ip mikusb_vps_game_${USER_UID} {
  chain output {
    type nat hook output priority -100; policy accept;
    meta skuid ${USER_UID} ip daddr ${GAME_TARGET_IP} tcp dport 5200 counter dnat to ${VPS_IP}:5200 comment "mikusb-vps-game-v1"
  }
}
MIKUSB_NFT
sudo chmod 644 "$ROUTE_RULES"

if [[ -d /run/systemd/system ]] && command -v systemctl >/dev/null 2>&1; then
  sudo tee "/etc/systemd/system/$ROUTE_UNIT" >/dev/null <<MIKUSB_SYSTEMD
[Unit]
Description=MikuSB game TCP redirect for UID ${USER_UID}
After=network.target
[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/bin/bash ${ROUTE_HELPER} apply ${USER_UID}
ExecStop=/usr/bin/bash ${ROUTE_HELPER} remove ${USER_UID}
[Install]
WantedBy=multi-user.target
MIKUSB_SYSTEMD
  sudo systemctl daemon-reload
  sudo systemctl enable "$ROUTE_UNIT"
  sudo systemctl restart "$ROUTE_UNIT"
  printf '\nGame redirect installed and enabled at boot.\n'
else
  sudo bash "$ROUTE_HELPER" apply "$USER_UID"
  printf '\nGame redirect installed for this boot; rerun setup after reboot.\n'
fi
printf 'Game TCP for UID %s: %s:5200 -> %s:5200\n' "$USER_UID" "$GAME_TARGET_IP" "$VPS_IP"
printf 'Restart Snowbreak and sign in. Remove this redirect with: bash %s --remove-game-route\n' "$0"
