# Promptly application and delivery audit — 2026-09-07

Promptly's accepted `main` cannot yet deliver the documented evaluation
workflow. Most defects already have open work, and 15 unmerged draft PRs
contain substantial remediation. The best next step is to recover a verified
foundation, then complete test authoring and Run Suite behavior as separate
reviewable pieces. The [four-hour plan](../plans/2026-09-07-four-hour-delivery.md)
sets the order and stopping conditions.

The audit targets `Jacob-J-Thomas/Promptly` at
`849a00b113ae694f3e8997b212e7e8786378ad0c`. The selected workspace was empty;
it was cloned from live GitHub. Old checkouts, branches, issues, and PRs were
preserved. No application source was changed. New files implement the
requested plugin, delivery guidance, diagnostic preflight, and these reports.

## What was actually verified

| Check | Result | Evidence and limit |
| --- | --- | --- |
| Live source, issues, PRs, parentage, protection and alerts | Captured | [Backlog snapshot](evidence/2026-09-07/backlog-summary.json), [exact PR heads/bases/checks](evidence/2026-09-07/pr-snapshot.json). Counts precede the new pipeline issue #73. |
| Frontend release build | FAIL: 23 TypeScript errors | [Build log](evidence/2026-09-07/web-build.txt); real source errors after rerunning with writable TypeScript cache. |
| Frontend lint | FAIL: 46 errors, 6 warnings | [Lint log](evidence/2026-09-07/web-lint.txt). |
| Locked dependency install | Unavailable as a fresh-install proof | Initial install hit host ENOSPC. Removed only this audit's partial installation; reused existing dependencies whose lockfile matched. This does not establish clean-install reproducibility. |
| Worker entrypoint | FAIL | `uvicorn main:app` exits because `main.py` is empty; [worker probes](evidence/2026-09-07/worker-probes.txt). |
| Worker router contracts | Executed, multiple defects | Isolated FastAPI router harness with synthetic inputs and fake SDK responses; [worker QA](evidence/2026-09-07/worker-qa.md). This is not a deployed worker or live-provider test. |
| API/application/SDK/CLI | Source review only | [15 grouped findings](evidence/2026-09-07/api-findings.json), [review](evidence/2026-09-07/api-source-review.md). No live PostgreSQL-backed authorization or run test was completed. |
| Full Compose startup | UNAVAILABLE | [Runtime attempt](evidence/2026-09-07/runtime-attempt.md): containerd metadata I/O failure, EXT4 aborted journal, host storage nearly full; no audit containers created. |
| Product unit coverage and hosted acceptance | MISSING on audited main | No committed product tests, coverage inventory, CI files or authoritative verifier on that SHA. Coverage is unmeasured, not a passing zero-test result. |

The build's failures include MUI Grid API incompatibilities, a type-only React
import, nullable icon types, and unused placeholder state. Vite can render the
development app despite these errors; browser rendering is not release proof.
Earlier subagent notes about unavailable frontend tooling were superseded by
the completed build/lint runs above.

## Browser user-flow probes

Executed 15 browser scenarios against the unmodified frontend. Unauthenticated
navigation and validation used the actual Vite app; authenticated routes used
browser-intercepted synthetic API responses. The [step ledger and fixture](evidence/2026-09-07/browser/qa-evidence.json)
identify every result and artifact. A UI-layer pass does not prove backend
persistence, provider calls, or full-stack acceptance.

| Scenario | Observed result | Tracking |
| --- | --- | --- |
| Missing session, registration validation, API unreachable | Protected root redirects to login; mismatched/short passwords show alerts. API requests fail with connection refused and generic visible errors. | #24/#67; real auth integration remains unavailable |
| Project/environment setup | Project list/dialog and required name validation render. A server-shaped environment with headers incorrectly says no custom headers are configured. | #37 |
| Mapping wizard | Required validation, proposal, Monaco edit/preview, validation and save/default calls complete with controlled fixtures. This does not prove the real mapping worker works. | #1/#13/#15/#25 |
| Create/edit a test | Create submits fixed empty messages and expectations; no editor fields exist. Edit closes its menu and logs to console without opening an editor. | #4/#10–12/#41/#44 |
| YAML import/export | File upload, success UI and download work against fixtures; no persisted or semantic round trip was proved. | #12/#44 |
| Run Suite | Dialog says it is a placeholder; selectors never populate and Start Run remains disabled. [Screenshot](evidence/2026-09-07/browser/run-dialog.png). | #6/#9 |
| Actual server-shaped run statuses/results | Numeric status crashes RunsList (`status.toLowerCase is not a function`). RunDetail shows numeric labels and `/ NaN` expectation counts. [Screenshot](evidence/2026-09-07/browser/run-detail-server-dto.png). | #37 |
| Desktop, mobile, keyboard | Desktop root measured about 425 px in a 1200 px viewport; 390 px mobile login fits and tab order reaches the expected controls. MUI Grid and focused aria-hidden warnings observed. | #8/#16/#23; broader accessibility remains unverified |

Screenshots use only synthetic fixture accounts and data. Browser probes add
fresh reproductions to existing ownership; they do not create a second set of
product issues.

## Findings and existing ownership

These are grouped observations reconciled with existing issues, not 15 new
tickets or a claim that each row is one fix. Related mechanisms require
separate checks before their shared issue can close; none closes here. The
source JSON separates inherited tracker priority from this audit's assessment.
Source findings are explicitly different from executed reproductions.

| User impact | Evidence class | Existing issue / candidate |
| --- | --- | --- |
| Cannot build a frontend release; quality gate is absent on main | Executed build/lint | #23 / PR #50; #18/#19/#22 for tests and enforcement |
| Worker never boots; mapping and judge routes unavailable | Executed entrypoint failure | #25 / PR #51 |
| Missing provider key becomes HTTP 200, score zero; infrastructure failure appears as an ordinary failed assertion | Executed isolated worker router, source C# consumer | #41, #25, #37 / PR #51 |
| Empty or malformed provider mapping specs are accepted | Executed fake-provider router | #1, #25, #37; strict saved mapping candidate PR #53 |
| Provider selection silently falls through for unknown/case variants; synchronous SDK calls serialize concurrent requests | Executed router/factory harness | #13/#14/#25/#46 |
| Supplied sample request is omitted from proposal prompt | Executed captured synthetic prompt | Attach evidence to #1/#25; no duplicate issue |
| Nested IDs are not scoped to the authenticated tenant; run resource graph is not checked | Source review, not a live exploit test | #26 / PR #57; #27 / PR #59 |
| Public default JWT key and unrestricted outbound endpoint destinations | Source/config review | #29 / PR #56; #28 / PR #60; #34 |
| API keys cannot authenticate documented SDK/CLI flow; base URL examples omit the API prefix | Source contract review | #40, #37, #45 |
| Run claiming is not atomic; queued inputs and historical deletion need a durable contract | Source review | #38, #39 |
| Empty expectations can pass, missing expectation fields use permissive defaults, exact tool-sequence behavior is wrong | Source review | #41, #44; bounded regex PR #48 belongs to #31 |
| Trace serialization/summary key casing and unmeasured latency produce inaccurate zero metrics | Source review | #42 |
| YAML/spec validation and duplicate ExternalId failure handling are incomplete | Source review | #44 |
| Worker container reload/root/exposure/readiness configuration is unsuitable for production | Source/Compose inspection | #25/#34/#46/#68; preserve local #68 candidate |

Worker negative probes also exercised malformed trace messages (422) and
invalid sample JSON (400), which behaved as expected. Two concurrent fake
250 ms SDK calls took 0.512 seconds, demonstrating event-loop blocking; this
measurement is diagnostic and does not benchmark a live provider.

## Delivery state

The pre-mutation snapshot contains 56 open issues and zero closed issues, with
53 in the Production Readiness milestone. There are 15 draft PRs, no approved
GitHub reviews, and currently successful reported candidate checks. Main is
unprotected, rulesets are empty, and its tree contains no workflow files.
Seven workflows are registered from candidate branches. Thus candidate green
checks are real historical evidence, but not proof that main is governed or
that all required acceptance receipts and review dispositions are present.

Preserve this order, checking the immediate parent for each effective patch:

`#48 → #50 → #51 → #52 → #53 → #54 → #55 → #56 → #57 → #59 → #60 → #65 → #66 → #70 → #71`

Current-head review comments exist on several candidates. Some have replies
or dismissals, which must be reconciled with the actual code; do not label
every old comment an unresolved blocker or assume an author reply proves it
fixed. No application candidate was accepted, merged, restacked, or closed by
this audit.

The newest #71 source still contains a placeholder run dialog and creates
tests with empty input and expectations. Even recovering the whole stack
would leave product completion work.

The paginated live dependency ledger contains 87 open alerts: 1 critical,
33 high, 46 medium and 7 low. This is default-branch debt, primarily npm, plus
the critical Data Protection package finding. Existing #21/#52 owns dependency
remediation. Current dependency evidence must be refreshed on candidates;
older candidate scans do not clear main's current alert ledger.

Existing native parents are #6→#9–12, #1→#13–15, #30→#61–64, #24→#67,
#21→#68, and #19→#69. #47 is a textual audit umbrella with no native children.
There are no native blocked-by edges in the captured snapshot. The proposed
AIDLC graph must be admitted deliberately, preserving these owners.

## Changes to the delivery pipeline

The new [plugin](../../plugins/promptly-aidlc/skills/promptly-aidlc/SKILL.md),
[repository instructions](../../AGENTS.md),
[delivery conventions](../AIDLC_DELIVERY_CONVENTIONS.md),
[verification contract](../VERIFICATION.md), PR template and preflight now:

- require testing transitions through the real user journey, including
  malformed input, refresh, error/retry, tenant boundaries and false success;
- record exact source/environment, expected/actual behavior, safe artifacts,
  evidence class, duplicate issue/PR and disposition;
- distinguish fixture probes, source review, focused tests and composed
  acceptance; missing tests, receipts or infrastructure stay non-passing;
- preserve the existing stack and native ownership, with deliberate
  Campaign → Phase → UOW → Bolt admission for future governed work;
- require exact-head evidence, meaningful component coverage, independent
  full review and bounded targeted review of actual fixes;
- support bounded recovery and independent parallel lanes while keeping one
  accountable integration owner and an explicit authority boundary.

The unpublished cross-repository overlay was inspected as prior work. It was
not overwritten or imported as an authoritative shared pipeline. This task
creates the Promptly-specific version and does not change the Agenthome skill.

## Limits and next action

Docker started from an initially stopped Colima profile, but the attempted
isolated stack failed before container creation. Storage repair, deletion of
retained Docker data, or a Colima reset was not attempted. The runtime report's
port observations are from that attempt; the frontend was subsequently run
independently on loopback for browser QA. No live LLM call was made.
After evidence capture, the isolated browser session was closed and the
audit-started Colima VM was stopped without deleting its data.

Full onboarding-to-results integration, real persisted round trips, two-user
authorization tests, worker/provider interoperability and SDK/CLI terminal
run behavior remain unverified. A healthy runtime or runner is the first
dependency for acceptance. Continue through the linked four-hour plan only
when execution is authorized; keep its work separate from this governance PR.
