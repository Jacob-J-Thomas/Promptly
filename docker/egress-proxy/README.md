# Promptly egress boundary

The Compose deployment gives the application, evaluator, database, and web UI
only the internal `promptly-control` network. `promptly-egress-proxy` is the
only dual-homed service. The named endpoint HTTP client is configured with
`EndpointEgress__ProxyUrl`; ambient proxy variables are deliberately cleared in
the server so database, evaluator, health, and other server HTTP clients cannot
inherit this route. The evaluator's provider SDK currently relies on
container-scoped `HTTP_PROXY`/`HTTPS_PROXY`; its `NO_PROXY` keeps localhost and
control-plane service calls off the egress route.

Smokescreen is built from commit
`131fba29ce1e3c2b9d78f359a2fb633703d5013d`. Its source archive and both build
images are integrity-pinned. A checked-in Go module overlay locks remediated
dependency versions, the relevant Go tests run during the image build, and a
version-pinned `govulncheck` scans all three binaries that enter the runtime image.
The checked-in configuration is parsed before that image is produced.

The hostname ACL is open so Promptly can test arbitrary public APIs. Address
classification is authoritative at Smokescreen's dial boundary: non-global,
private, loopback, link-local, CGNAT, reserved/documentation, IPv4-embedded IPv6,
and known metadata addresses are denied. The application also validates targets
before decrypting credentials, disables redirects, and uses the proxy explicitly.

## Limitations

- Compose network isolation is the supported boundary in this repository. A
  Kubernetes, cloud, or host-native deployment must reproduce the same default-
  deny routing with its platform's network policy; copying only the proxy config
  is not sufficient.
- The evaluator currently has no Promptly-specific provider-proxy setting. Its
  provider libraries therefore receive proxy variables scoped to that container.
  Adding other outbound clients to the evaluator requires checking that they
  should share the same route and that control-plane hosts remain in `NO_PROXY`.
- Operators must not enable `unsafe_allow_private_ranges`, attach application
  containers to `promptly-egress`, or set ambient `HTTP_PROXY`, `HTTPS_PROXY`, or
  `ALL_PROXY` variables. The application rejects non-public allow rules whenever
  a proxy is configured because Smokescreen's address allows cannot preserve
  Promptly's exact host-and-port isolation. Use direct mode behind an equivalent
  deployment network boundary for those reviewed rules. Link-local and metadata
  targets are permanently unavailable.

Run the deterministic policy proof from the repository root:

```sh
./scripts/verify-egress-policy.sh
```
