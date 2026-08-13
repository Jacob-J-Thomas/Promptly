# Production-composed browser verification

This gate proves only the currently implemented authentication/session boundary:

- anonymous protected-route redirect;
- browser registration, Projects arrival, logout, and login through the real API;
- a deterministically expired, test-signed JWT receiving a real protected-API 401,
  redirecting to login, and clearing both stored auth entries.

It does not claim the remaining project-to-result, CRUD/navigation, demo, or
mapping journeys tracked by #24 and their product blockers.

## Exact local command

Prerequisites are Docker with Compose, Node from `.node-version`, and the pinned
Chromium runtime:

```sh
cd Promptly.Web/src/web
npm ci
npx playwright install chromium
npm run test:e2e
```

`npm run test:e2e` is the only supported entry point. It generates unique
Compose project state, high-entropy database/JWT secrets, and free loopback web
and API ports; builds the static web, Production server, worker, provider stub,
and egress proxy; waits with fixed bounds; runs the exact required-test inventory;
collects evidence; and unconditionally removes the containers, networks, volumes,
and per-run images. It never calls a reset endpoint and never uses production
credentials.

Artifacts are written under `artifacts/test-results/e2e`:

- `junit.xml`, `results.json`, `verification.json`, and `html/index.html`;
- retained failure traces, screenshots, and videos under `playwright-output`;
- sanitized provider requests, Compose logs/state/images, topology and cleanup
  attestations, runner log, and run metadata.

Upload finalization recursively inspects retained trace ZIP contents and scrubs
generated bearer tokens/passwords plus per-run Compose secrets before creating the
`upload-safe.json` marker. Nested or oversized archives fail closed.

## Retry and quarantine policy

Retries are zero. Required tests may not be focused, skipped, fixed, marked as
expected failures, tagged, quarantined, flaky, duplicated, omitted, or added
without updating `required-tests.json`. The verifier fails closed on any drift.
If a product defect blocks a required journey, open and link an issue, repair the
defect, and keep the test required; do not hide it with retries or quarantine.

The browser guard permits only the generated web origin and treats page errors,
console errors, request failures, unapproved HTTP failures, and browser egress as
test failures. The expired-session test registers the single expected 401 before
triggering it and must observe that exact response.

The topology gate first proves its globally routed canary is reachable through the
allowed egress proxy and that each direct-egress probe is executable with a
same-container control-network connection. It then requires an exact bounded-timeout
contract to that same canary without the proxy. The host-ingress network has IPv6
disabled and IPv4 masquerading disabled; arbitrary command failures are not accepted
as egress-isolation evidence.
