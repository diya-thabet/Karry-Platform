#!/bin/sh
# Starts the local OpenBao (ADR-0033 §3) and makes it ready to sign:
# - first start: initialise with one unseal key, kept in its own volume (/openbao/keys), not in the data volume;
# - every start: unseal (file storage starts sealed), enable transit, create the ecdsa-p256 key "karry-server"
#   (not exportable) if missing, write the "karry-signer" policy (sign and read public keys, nothing else), and
#   create or renew the API's token with that policy. The root token never leaves the container.
# Local stack only: one unseal key and a known signer token are fine for throwaway local keys, never elsewhere.
set -eu
export BAO_ADDR=http://127.0.0.1:8200
keys=/openbao/keys/init.txt
signer_token=${KARRY_OPENBAO_SIGNER_TOKEN:?set KARRY_OPENBAO_SIGNER_TOKEN}

rm -f /tmp/ready
mkdir -p /openbao/keys
chmod 700 /openbao/keys
chown -R openbao:openbao /openbao/file

su-exec openbao bao server -config=/etc/openbao/openbao.hcl &
server=$!
trap 'kill -TERM "$server"; wait "$server"' TERM INT

until wget -q -O /dev/null "$BAO_ADDR/v1/sys/seal-status" 2> /dev/null; do
  kill -0 "$server" 2> /dev/null || { echo "openbao: the server stopped" >&2; exit 1; }
  sleep 1
done

if ! (bao status -format=json || true) | grep -q '"initialized": true'; then
  bao operator init -key-shares=1 -key-threshold=1 > "$keys"
  chmod 600 "$keys"
  echo "openbao: initialised; unseal key kept in the openbao-keys volume"
fi

bao operator unseal "$(sed -n 's/^Unseal Key 1: //p' "$keys")" > /dev/null
BAO_TOKEN=$(sed -n 's/^Initial Root Token: //p' "$keys")
export BAO_TOKEN

bao secrets list | grep -q '^transit/' || bao secrets enable transit
bao read transit/keys/karry-server > /dev/null 2>&1 || bao write -f transit/keys/karry-server type=ecdsa-p256 exportable=false > /dev/null
bao policy write karry-signer - > /dev/null <<'POLICY'
path "transit/sign/karry-server" {
  capabilities = ["update"]
}
path "transit/keys/karry-server" {
  capabilities = ["read"]
}
POLICY
if bao token lookup "$signer_token" > /dev/null 2>&1; then
  bao token renew "$signer_token" > /dev/null
else
  bao token create -id="$signer_token" -policy=karry-signer -orphan -period=768h -display-name=karry-signer > /dev/null
fi
unset BAO_TOKEN

touch /tmp/ready
echo "openbao: unsealed, key karry-server ready"
wait "$server"
