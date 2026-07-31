# Forwarded headers (OWNER-ACTION 10)

Caddy must preserve its reverse-proxy defaults for `X-Forwarded-For` and
`X-Forwarded-Proto`. PartyUp accepts exactly one forwarded hop.

Read the subnet of the Docker Compose network used by Caddy and set it in the
VM environment, for example:

```dotenv
Network__TrustedProxies__0=172.20.0.0/16
```

The value must follow the deployed Compose network; do not copy the example
subnet without checking it. An absent or stale value makes every IP-partitioned
rate limit see Caddy's container address instead of the caller.
