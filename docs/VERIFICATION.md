# Verification and evidence contract

This repository is bootstrapping its delivery gates. On the audited `main`
baseline (`849a00b113ae694f3e8997b212e7e8786378ad0c`), no committed
`.github/workflows` directory, test projects or browser suite, coverage
configuration or receipts, or authoritative `verify` script was present. The
absence is evidence for planning. It must not be hidden by a new script that
reports a green result from a partial local check.

Use the read-only inventory first:

```bash
python3 scripts/aidlc_preflight.py --format text
python3 scripts/aidlc_preflight.py --format json > /tmp/promptly-preflight.json
```

The preflight output is diagnostic. It reports the exact repository/head,
worktree, test and workflow inventory, tool availability, and each missing or
failed evidence source. Its default exit code indicates that the inventory ran;
`--strict` makes missing or failed evidence a nonzero result for an explicit
audit. Neither mode is an acceptance gate.

## Gate layers

Run the narrowest useful layer during development and the complete applicable
set before acceptance.

| Layer | Evidence | Current status |
| --- | --- | --- |
| Preflight | Exact repository identity, SHA, worktree, files, tests, workflows, tool availability | Available through `aidlc_preflight.py`; diagnostic only |
| Focused static | .NET restore/build/format, Python syntax/type checks, frontend lint/build, Compose config | Commands may be run locally; no authoritative receipt exists yet |
| Component behavior | .NET application/domain/infrastructure/server tests, Python worker tests, frontend behavior tests, SDK/CLI tests | Missing at baseline; schedule isolated test Bolts |
| Full-stack behavior | Isolated PostgreSQL/API/worker/web stack, migrations, auth, tenant boundaries, run lifecycle, browser journey | Missing at baseline; fixed Compose names/ports require an isolated test topology |
| Security and dependency | Vulnerable/transitive dependency audit, secret/config checks, CodeQL or equivalent, API authorization probes | Missing at baseline; do not infer from restore or compilation |
| Coverage | Meaningful tests and a receipt for every production component, target >=90% | Missing at baseline; report numerator/denominator and exclusions |
| Hosted exact-head | CI job/attempt, artifact or log receipt, input SHA, environment, duration, and result | Missing at baseline; a future workflow must bind every receipt to the PR head |
| Independent review | One full review and, when a fix delta exists, one fix-delta review by an independent reviewer | Per-PR evidence; absent until recorded against the exact head |

The repository's eventual full gate must be admitted as its own implementation
work. It should compose the focused checks, isolated behavior tests, security
and dependency checks, coverage collection, and receipt generation. Do not
duplicate an existing admitted verifier or call a quick build a full gate.

## Focused command matrix

These commands are useful diagnostic probes while the authoritative gate is
being built. Record failures and environment prerequisites:

```bash
dotnet restore Promptly.slnx
dotnet build Promptly.slnx --configuration Release --no-restore
dotnet format Promptly.slnx --verify-no-changes --no-restore
python3 scripts/aidlc_preflight.py --format text
npm --prefix Promptly.Web/src/web ci
npm --prefix Promptly.Web/src/web run lint
npm --prefix Promptly.Web/src/web run build
docker compose -f docker/docker-compose.yml config --quiet
```

The solution includes a Visual Studio Python project file that may not build
on every host. A successful .NET build therefore does not prove worker
behavior. `npm ci` and a frontend build do not prove browser behavior. Compose
configuration does not prove that PostgreSQL migrations, worker calls, or the
user journey works. Keep those claims separate in the evidence ledger.

## Exact-head receipt

Every required gate receipt must identify:

```yaml
repository: Jacob-J-Thomas/Promptly
candidate:
  branch: <branch>
  pullRequest: <number or null>
  baseSha: <40-hex SHA>
  headSha: <40-hex SHA>
  effectiveParent: <immediate parent SHA>
gate:
  id: <stable gate id>
  commandOrJob: <exact command or hosted job URL/id>
  environment: <OS, runtime, dependency/container versions>
  startedAt: <UTC timestamp>
  durationSeconds: <number>
  inventory: <tests/components/checks actually run>
  result: PASS | FAIL | MISSING | SKIPPED | UNAVAILABLE
  receipt: <artifact path, digest, or hosted receipt>
  reason: <required for every non-PASS result>
```

After a restack, conflict repair, amend, merge, or material fix, re-read the
candidate head/base and repeat evidence for the changed effective patch. A
timeout, cancellation, runner outage, stale receipt, partial inventory, or a
job for another SHA is diagnostic evidence and stays non-passing.

## Acceptance boundary

A Bolt can be accepted only when its exact current head has:

- all required gates for its changed systems with complete inventory and fresh
  receipts;
- meaningful automated coverage at or above 90% for every affected production
  component, or an explicit contract-approved no-change rationale for a
  component that has no executable code path;
- isolated behavior evidence for the user journey and relevant failure paths;
- dependency, security, and configuration evidence appropriate to the change;
- one independent full review and, when the full review caused a fix delta, one
  targeted review of that final delta;
- every review root cause disposed as `FIX-IN-PR`, `DEFER-ISSUE`, `NO-CHANGE`,
  or `DUPLICATE-STALE`, with no unresolved P0/P1 blocker;
- revalidated parentage, ownership, stack ancestry, invariants, and closure
  target; and
- active-contract merge authority covering the in-scope exact head if a merge
  is requested.

Missing infrastructure is a planned work item. It is never silently treated as
success. Until the required gates exist, use `MISSING`, `UNAVAILABLE`, or
`DIAGNOSTIC` in reports and keep the PR open.
