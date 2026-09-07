# Aggressive user QA and finding discovery

Treat Promptly as a user would: start from a clean browser session, use the documented UI, submit realistic values, reload between steps, and confirm the result through the UI and the API/database boundary that owns it. Prefer a real Docker Compose stack and the built-in demo endpoint. Record environment, commit SHA, service versions, and whether the observation is static, API-level, browser-level, or end-to-end.

## Journey matrix

Walk each journey far enough to find broken transitions, misleading success, lost state, and unsafe defaults:

1. Register, duplicate registration, login, bad credentials, refresh, logout, expired/missing token, and protected route handling.
2. Create, view, update, and delete a project; reload and check ownership and empty-state behavior.
3. Create an environment with headers, inspect redaction/encryption behavior, add an endpoint, validate method/path/timeout/base URL, and exercise malformed or unreachable targets.
4. Open the mapping wizard with valid and invalid sample JSON, worker unavailable, incomplete proposal, validation failure, save/default/delete, and a response with messages, tool calls, usage, documents, or fallback content.
5. Create a suite and test, edit an existing test, delete it, import valid/invalid/duplicate YAML, export it, reload, and verify input and expectation data survive the round trip.
6. Configure and queue a run with real environment/endpoint/mapping selections, empty suite, mismatched resource IDs, worker failure, endpoint timeout, mapping failure, deterministic expectation failure, judge failure, and repeated polling.
7. Inspect run history, completed/failed/queued states, summary counts, per-test expectations, canonical trace, raw response limits, error messages, and navigation after refresh.
8. Use SDK/CLI paths when in scope, including authentication failures, timeout behavior, malformed API responses, and a run that can be followed to terminal state.

For every journey, check keyboard/focus behavior, disabled buttons, validation text, loading/retry behavior, browser console errors, network status and payload, route refresh/deep links, and whether an apparent success actually persisted. A green toast or HTTP 200 is not proof of a completed user outcome.

## High-value risk probes

Probe tenant boundaries by attempting another user's IDs and verify a safe 404/403 without data leakage. Probe endpoint execution for localhost/private-network targets, redirect behavior, timeouts, header handling, and oversized responses. Probe authentication for duplicate users, weak credentials, token expiry, and secrets in logs or API responses. Probe YAML/JSON parsing for invalid, deeply nested, oversized, or duplicate data. Verify LLM-assisted routes fail closed with structured errors when the worker or provider is unavailable; never replace the worker with a fake response to make a user journey pass.

## Finding record

Capture one record per root cause:

```text
Title:
Journey and component:
Commit / environment / service state:
Preconditions and exact steps:
Expected:
Actual:
Evidence: URL, request/response status and safe payload excerpt, screenshot/log/test artifact
Frequency: deterministic / intermittent / not reproduced
Impact: P0-P3 with a concrete user or security consequence
Likely root cause and reachable source path:
Duplicate search terms and related issue/PR IDs:
Disposition: FIX-IN-PR / DEFER-ISSUE / NO-CHANGE / DUPLICATE-STALE
```

Use P0 for data loss, secret exposure, tenant escape, or a completely unusable core path; P1 for a common documented journey that cannot complete or records false success; P2 for a meaningful workaround or narrow broken path; P3 for polish, diagnostics, or low-impact debt. Calibrate severity from observed impact, not from the number of changed lines.

Before tracking an issue, search existing open issues and pull requests by route, error text, component, and symptom. Attach the minimal repro and exact evidence, link related work, and preserve the distinction between an observed defect, an unfinished feature, a test gap, and a product decision. Do not file speculative tickets for every TODO or duplicate a review comment.

## Evidence quality

An unavailable dependency is a preflight or infrastructure finding until the product behavior is tested with a healthy fixture. A static contradiction is a lead until its reachable behavior is shown. A mocked HTTP response can validate a caller's parser but cannot establish worker or all-stack acceptance. When a browser check fails, retain screenshot, console/network details, URL, and server logs before retrying.

Use synthetic accounts and payloads. Before retaining or publishing artifacts, redact credentials, authorization/cookie headers, environment secrets and personal data from logs, screenshots, request/response bodies and traces. Review the final issue/PR evidence for accidental disclosure. Prefer a minimal sanitized excerpt over a raw trace containing authenticated traffic.
