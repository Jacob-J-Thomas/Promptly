# Browser discovery reproduction and evidence limits

Observed 2026-09-07, approximately 21:31–21:45 UTC, against application source
`849a00b113ae694f3e8997b212e7e8786378ad0c`. Runner: Playwright CLI 0.1.18
(cached package with Playwright 1.63.0-alpha-2026-08-05), headed session
`promptly-qa`. The exact browser build was not retained. This is discovery
evidence, not a reproducible full-stack acceptance receipt.

The actual frontend launch was `npm run dev -- --host 127.0.0.1` from
`Promptly.Web/src/web`, serving port 3000. Existing dependencies were reused
after fresh installation failed with ENOSPC; their package-lock matched main.
Vite reported 7.3.1. Host tooling was Node v22.22.2, npm 10.9.7, macOS 26.3.1 on arm64. API port 5000 was unavailable throughout.

The bundled Playwright shell wrapper had incompatible line endings, so the
existing cached CLI was used directly:

```text
node /Users/jake/.npm/_npx/31e32ef8478fbf80/node_modules/@playwright/cli/playwright-cli.js --session promptly-qa open http://localhost:3000 --headed
node /Users/jake/.npm/_npx/31e32ef8478fbf80/node_modules/@playwright/cli/playwright-cli.js --session promptly-qa snapshot
```

Subsequent commands used snapshot refs for click/fill/keyboard actions and
`run-code` for API route fixtures and assertions. The fixture expression in
`install-fixtures.js` requires an already open page at
`http://localhost:3000/login`: its localStorage call needs that origin. It is
not a standalone Node program and must not be installed on a real deployment.
Its synthetic token/header strings cannot authenticate to the server. The
route handler intercepts `http://localhost:5000/api/**` only.

To repeat a fixture probe with an installed CLI, open and snapshot the page,
pass the file contents as a single `run-code` argument, then interact through
fresh snapshots. For example, Python's `subprocess.run` with a list of arguments
can pass `Path('install-fixtures.js').read_text()` without shell interpolation.
Inspect the environment and numeric run/result DTO fixtures, open Run Suite,
create/edit a test, and repeat import/export. Reproduce actual API and database
behavior separately on a healthy stack before acceptance.

`qa-evidence.json` maps individual steps to available artifacts. Some client
validation observations have no separately retained screenshot and are labeled
accordingly. `console-snippets.txt` preserves the connection-refused, Edit,
numeric-status crash and MUI diagnostics. No HAR was retained; successful
fixture request handling is not a captured production network exchange.
Desktop viewport was 1200×829 and mobile login was 390×844. The named browser
session and audit frontend process were closed after capture.

Some navigation fixtures intentionally include both endpoint method aliases to reach later UI steps. They are not server-contract tests. The header, numeric status and result-field defect probes use the server-shaped variants identified in the ledger; repeat those against real API DTOs before acceptance.
