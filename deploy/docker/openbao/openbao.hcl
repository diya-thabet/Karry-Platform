# Local OpenBao for the server signing key (ADR-0033 §3): integrated (raft) storage on a volume, one node, so keys
# survive restarts (dev mode keeps them in memory and a restart would make every countersignature unverifiable;
# OpenBao 2.x has no "file" storage). Local only: no TLS. Production uses the cloud KMS (P1.17).
storage "raft" {
  path    = "/openbao/file"
  node_id = "karry-local"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr     = "http://127.0.0.1:8200"
cluster_addr = "http://127.0.0.1:8201"
ui           = false
