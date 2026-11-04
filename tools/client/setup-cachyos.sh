#!/usr/bin/env bash
set -euo pipefail

# Configure a local or remote MikuSB server. The server's public certificate
# must be supplied locally, or fetched after verifying its SHA-256 fingerprint.
SERVER_HOST="${MIKUSB_SERVER_HOST:-${MIKUSB_VPS_HOST:-${MIKUSB_VPS_IP:-127.0.0.1}}}"
SERVER_GAME_PORT="${MIKUSB_GAME_PORT:-21000}"
SDK_PORT="${MIKUSB_SDK_PORT:-13443}"
GAME_TARGET_IP="${MIKUSB_GAME_TARGET_IP:-49.51.66.126}"
CERT_SOURCE="${MIKUSB_CERT:-}"
FINGERPRINT=""
FETCH_CERT=0
REMOVE_ROUTE=0
die() { printf 'setup-cachyos: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"; }
usage() {
  cat <<'USAGE'
Usage: setup-cachyos.sh [--host HOST] [--game-port PORT] [--sdk-port PORT]
       [--cert PUBLIC_PEM | --fetch-cert --fingerprint SHA256]
       setup-cachyos.sh --remove-game-route

Defaults: host 127.0.0.1, game port 21000, SDK probe port 13443.
--cert accepts the server's public wildcard.xoyo.games.pem. For remote
servers, --fetch-cert downloads the public certificate from the TLS endpoint
and checks the fingerprint supplied by the server administrator.
USAGE
}
while [[ $# -gt 0 ]]; do
  case "$1" in
    --host|--game-port|--sdk-port|--cert|--fingerprint)
      [[ $# -ge 2 && -n "$2" ]] || die "Missing value for $1"
      case "$1" in
        --host) SERVER_HOST="$2" ;;
        --game-port) SERVER_GAME_PORT="$2" ;;
        --sdk-port) SDK_PORT="$2" ;;
        --cert) CERT_SOURCE="$2" ;;
        --fingerprint) FINGERPRINT="$2" ;;
      esac
      shift 2 ;;
    --fetch-cert) FETCH_CERT=1; shift ;;
    --remove-game-route) REMOVE_ROUTE=1; shift ;;
    --help|-h) usage; exit 0 ;;
    *) die "Unknown argument: $1" ;;
  esac
done
[[ "$EUID" -ne 0 ]] || die 'Run this script as your normal Steam user, not root.'
need sudo
need python3
need openssl
need getent
need nft
need curl

USER_UID=$(id -u)
ROUTE_UNIT="mikusb-game-route-${USER_UID}.service"
ROUTE_HELPER=/etc/mikusb-vps/game-route.sh
ROUTE_RULES="/etc/mikusb-vps/game-route-${USER_UID}.nft"
prepare_writable() {
  if command -v steamos-readonly >/dev/null 2>&1 && steamos-readonly status | grep -qi enabled; then
    sudo steamos-readonly disable
    trap 'sudo steamos-readonly enable' EXIT
  fi
}

if [[ "$REMOVE_ROUTE" == 1 ]]; then
  prepare_writable
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
[[ "$FETCH_CERT" == 0 || -z "$CERT_SOURCE" ]] || die 'Use either --cert or --fetch-cert.'
[[ "$FETCH_CERT" == 1 || -z "$FINGERPRINT" ]] || die '--fingerprint requires --fetch-cert.'
need timeout
if [[ "$FETCH_CERT" == 1 ]]; then
  FINGERPRINT=$(printf '%s' "$FINGERPRINT" | tr -d ':' | tr '[:lower:]' '[:upper:]')
  [[ "$FINGERPRINT" =~ ^[0-9A-F]{64}$ ]] || die '--fetch-cert requires the server certificate SHA-256 fingerprint via --fingerprint.'
fi
SERVER_GAME_PORT=$(python3 -c 'import sys; s=sys.argv[1]; assert s.isascii() and s.isdigit() and 1 <= int(s) <= 65535; print(int(s))' "$SERVER_GAME_PORT") || die 'Invalid game port.'
SDK_PORT=$(python3 -c 'import sys; s=sys.argv[1]; assert s.isascii() and s.isdigit() and 1 <= int(s) <= 65535; print(int(s))' "$SDK_PORT") || die 'Invalid SDK port.'
if [[ "$SERVER_HOST" != *[!0-9.]* ]]; then
  SERVER_IP="$SERVER_HOST"
else
  SERVER_IP=$(getent ahostsv4 "$SERVER_HOST" | awk 'NR==1 {print $1}')
  [[ -n "$SERVER_IP" ]] || die "Could not resolve server host: $SERVER_HOST"
fi
python3 -c 'import ipaddress, sys; [ipaddress.IPv4Address(x) for x in sys.argv[1:]]' "$SERVER_IP" "$GAME_TARGET_IP" \
  || die 'Server and game target addresses must be valid IPv4 addresses.'
[[ "$SERVER_IP" != "$GAME_TARGET_IP" ]] || die 'Server and game target addresses must be different.'

STATE="$HOME/.local/share/mikusb-vps"
mkdir -p "$STATE"
chmod 700 "$STATE"
CERT="$STATE/wildcard.xoyo.games.pem"
if [[ "$FETCH_CERT" == 0 && -z "$CERT_SOURCE" ]]; then
  for candidate in sdk-certs/wildcard.xoyo.games.pem MikuSB/bin/Debug/net10.0/sdk-certs/wildcard.xoyo.games.pem "$CERT"; do
    if [[ -f "$candidate" ]]; then CERT_SOURCE="$candidate"; break; fi
  done
fi
if [[ "$FETCH_CERT" == 0 ]]; then
  [[ -n "$CERT_SOURCE" && -f "$CERT_SOURCE" ]] || die 'Public certificate not found. Supply --cert PATH or --fetch-cert --fingerprint SHA256.'
  if grep -q -- 'PRIVATE KEY-----' "$CERT_SOURCE"; then die '--cert must contain a public certificate only, not a private key.'; fi
fi
CERT_TMP=$(mktemp "$STATE/certificate.XXXXXXXX")
if [[ "$FETCH_CERT" == 1 ]]; then
  if ! timeout 10 openssl s_client -connect "$SERVER_IP:$SDK_PORT" -servername xgsdk.xoyo.games -showcerts </dev/null 2>/dev/null | openssl x509 -out "$CERT_TMP"; then
    rm -f -- "$CERT_TMP"; die 'Could not fetch a certificate from the SDK TLS endpoint.'
  fi
  ACTUAL_FINGERPRINT=$(openssl x509 -in "$CERT_TMP" -noout -fingerprint -sha256 | cut -d= -f2 | tr -d ':' | tr '[:lower:]' '[:upper:]')
  if [[ "$ACTUAL_FINGERPRINT" != "$FINGERPRINT" ]]; then
    rm -f -- "$CERT_TMP"; die "Certificate fingerprint mismatch: received $ACTUAL_FINGERPRINT"
  fi
else
  if ! openssl x509 -in "$CERT_SOURCE" -out "$CERT_TMP"; then
    rm -f -- "$CERT_TMP"; die 'Could not read the public certificate.'
  fi
fi
openssl x509 -in "$CERT_TMP" -noout -subject -fingerprint -sha256
printf 'Verifying SDK access with the selected certificate...\n'
if ! curl --fail --silent --show-error --noproxy '*' --cacert "$CERT_TMP" \
  --resolve "xgsdk.xoyo.games:${SDK_PORT}:${SERVER_IP}" \
  "https://xgsdk.xoyo.games:${SDK_PORT}/getGameConfig"; then
  rm -f -- "$CERT_TMP"; die 'SDK connection or certificate verification failed; no routing changes were applied.'
fi
printf '\nChecking the game server TCP port...\n'
if ! timeout 5 bash -c 'exec 3<>"/dev/tcp/$1/$2"' _ "$SERVER_IP" "$SERVER_GAME_PORT"; then
  rm -f -- "$CERT_TMP"; die "Game server $SERVER_IP:$SERVER_GAME_PORT is unreachable; no routing changes were applied."
fi
mv -f -- "$CERT_TMP" "$CERT"
chmod 644 "$CERT"
prepare_writable
printf '\nAdding the selected public certificate to the host trust store...\n'
sudo install -Dm644 "$CERT" /etc/ca-certificates/trust-source/anchors/mikusb-vps.crt
sudo update-ca-trust extract

HOSTS_MARKER='# MikuSB VPS SDK routing (managed by setup-cachyos.sh)'
sudo sed -i "\\|$HOSTS_MARKER|d" /etc/hosts
printf '%s xgsdk.xoyo.games %s\n' "$SERVER_IP" "$HOSTS_MARKER" | sudo tee -a /etc/hosts >/dev/null

printf '\nMikuSB is reached directly; no SSH tunnel or proxy setting is needed.\n'
printf 'Snowbreak SDK hostname: xgsdk.xoyo.games -> %s\n' "$SERVER_IP"

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
    meta skuid ${USER_UID} ip daddr ${GAME_TARGET_IP} tcp dport 5200 counter dnat to ${SERVER_IP}:${SERVER_GAME_PORT} comment "mikusb-vps-game-v1"
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
printf 'Game TCP for UID %s: %s:5200 -> %s:%s\n' "$USER_UID" "$GAME_TARGET_IP" "$SERVER_IP" "$SERVER_GAME_PORT"
printf 'Restart Snowbreak and sign in. Remove this redirect with: bash %s --remove-game-route\n' "$0"
