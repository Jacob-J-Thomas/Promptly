# Exact-head verification and independent review

Acceptance evidence is bound to the exact candidate SHA. Before and after every material change, restack, merge, or conflict repair, re-query candidate head, base, ancestry, effective diff, and the live pull request. Preserve command, job or workflow identity, SHA input, environment, duration, inventory, receipt, and result.

## Gate inventory

Read the current `.github/workflows`, repository instructions, and verification documentation before naming required checks. At minimum, when the environment allows it, cover the affected layer and then the complete all-stack path:

- .NET restore/build/test and migrations against a disposable PostgreSQL fixture;
- frontend dependency install, type-check, build, lint/format, and meaningful behavior checks from `Promptly.Web/src/web`;
- Python syntax/import checks and a real worker health/API contract probe;
- Docker Compose config/build/up/health and a demo endpoint request;
- browser evidence for changed user journeys, with console and network output retained;
- security, dependency, and tenant/SSRF checks when their paths are reachable.

Build an explicit applicable-gate inventory from the work contract, changed systems, workflows and repository policy. GitHub's required-check list is only one input: an empty ruleset does not make acceptance vacuously green. Unknown applicability, missing required gates and unavailable receipts prevent acceptance. Record absent branch protection as a governance gap; do not ignore an applicable failing workflow because it is not marked required in GitHub.

Use the repository's named CI checks when they exist; do not invent check names. The default quality target is at least 90% meaningful per production component. Missing tests, coverage, inventory, Browser evidence, security/dependency evidence, or receipts are gaps or failures to record, never green substitutes. A local focused pass cannot certify a missing hosted or all-stack gate.

When a dependency is unavailable, distinguish preflight failure from an executed test failure. Do not conceal missing restore, runner outage, provider credentials, or a worker import error behind a mock. A timeout, cancelled job, stale result, cached output from another SHA, or partial inventory is diagnostic evidence only.

## Review protocol

Review the effective patch against its immediate parent, especially for stacked pull requests. The reviewer must be independent of the implementation author and co-author. Return:

- reviewed base/head and reachable source/behavior evidence;
- findings grouped by causal mechanism, with severity and whether they block the stated outcome;
- required repair or explicit disposition (`FIX-IN-PR`, `DEFER-ISSUE`, `NO-CHANGE`, `DUPLICATE-STALE`);
- residual uncertainty, untested paths, and evidence that remains missing.

Use one independent full review after required gates, then one targeted review only if an admitted fix delta exists. Use a third targeted pass only for a credible new P0/P1 introduced by those repairs. Do not repeat an unchanged review or treat a review comment as executable verification. Fix reachable merge blockers inside the admitted unit; deduplicate nonblocking debt as one tracked finding when authorized.

The final review receipt must identify the exact current head and immediate base. After a restack or material change, the older receipt remains evidence for its original patch only; it cannot alone certify the new candidate. Review the changed semantics/conflict delta and bind cumulative review evidence to the current pair. A patch-equivalent restack may reuse unchanged findings with explicit equivalence evidence, without forcing a new full review. Record every finding's disposition and leave no unresolved P0/P1 or other declared blocker. A bare COMMENTED review, author reply or absence of comments is not approval evidence.

## Acceptance and merge

The delivery owner reconciles review dispositions, required exact-head receipts, invariants, hierarchy and closure targets, and the intended PR order. Merge only with current explicit user authorization, exact current head/base, complete required green evidence, independent review, and branch protection intact. PRs remain open by default. After a merge, revalidate authoritative `main`, merge parents/tree identity, successor order, and rolled-up acceptance before closing any supported Bolt/UOW/Phase boundary.
