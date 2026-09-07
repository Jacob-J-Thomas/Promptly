# Four-hour Promptly delivery plan

Prepared 2026-09-07 from the [current audit](../audits/2026-09-07-promptly-qa.md).
Use `plugins/promptly-aidlc/skills/promptly-aidlc/SKILL.md` or the installed
`promptly-aidlc` plugin. The clock below starts when execution is authorized;
this document does not start a scheduled run or authorize merges.

The useful outcome is a verified development base and as much of the
onboarding-to-results workflow as can be finished with credible evidence.
Recover existing PRs before rebuilding their features. Aim for reviewed,
independently understandable candidates; do not promise all 56 existing
issues or all 15 draft PRs will be completed in four hours.

## Contract and prerequisites

- Existing audit owner: [#47](https://github.com/Jacob-J-Thomas/Promptly/issues/47).
  This pipeline bootstrap: [#73](https://github.com/Jacob-J-Thomas/Promptly/issues/73).
- Baseline audited: `849a00b113ae694f3e8997b212e7e8786378ad0c`; re-read live
  main, candidate heads/bases, native parents, checks, reviews, and worktrees.
- Preserve the existing draft stack, local supply-chain candidate #68, and
  old checkouts. No force pushes, wholesale issue migration, mass closure,
  or replacement PR train is part of this plan.
- Docker has storage I/O errors and the host ran out of space. Stop local
  container builds until headroom and Docker health are established. Use a
  healthy authorized runner for composed validation if available. Filesystem
  repair, deleting retained data, and replacing Colima are separate actions
  requiring an explicit safe recovery scope.
- Keep main as the ultimate integration target and preserve each stack PR's
  immediate parent. Changes to shared service/DTO files need one owner.
- Maximum concurrent work: two independent implementation lanes, one review
  lane; serialize heavy builds. Increase only after measuring available disk,
  memory, and hosted capacity.
- Merge authority is absent in the present request. Execution can prepare
  candidates and review; PRs remain open. If the user later delegates in-scope
  qualifying merges, honor that standing contract without asking per PR and
  still compare-and-match the reviewed exact head.

## Proposed work graph

Use a proposed Campaign for a reliable first evaluation, with a Phase for the
verified foundation and a Phase for authoring-to-results. These are planning
roles, not newly created GitHub parents. Read and preserve the native parents
of #47, #1, #6, #16 and their descendants before admitting any new graph.

UOWs are (a) foundation recovery, (b) usable test authoring, and (c) run setup
and truthful results. Existing issues and PRs retain their ownership. Each
new Bolt gets one native immediate parent when admitted and normally one PR.
Do not call an old aggregate a Bolt just to add a closing keyword.

## Time allocation

| Elapsed | Primary owner / integrator | Independent lane | Exit evidence |
| --- | --- | --- | --- |
| 0–20 min | Revalidate main/stack/authority and choose healthy execution environment. Read #48's reachable review findings and gate applicability. | Reviewer inventories exact head/base and receipt availability for the first recoverable prefix. | Explicit go/no-go for full-stack execution; immutable candidate ledger; no blind retry on damaged Docker storage. |
| 20–80 min | Recover the first coherent stack prefix: #48 then #50, #51 and #52 as dependencies and review findings permit. Keep existing PRs. | Inspect #53–#55 mapping/coverage/integration contracts; prepare a scoped #9 run-modal contract and acceptance cases. | Focused gates and existing receipts reconciled, relevant security findings dispositioned, refreshed checks where evidence is missing or obsolete. |
| 80–160 min | Continue #53–#55 only if preceding heads qualify. Reproduce actual API/worker contracts and composed smoke path on the healthy runner. | Implement #9 on the explicitly selected verified base, in a separate candidate; avoid editing SuiteDetail concurrently with editor work. If no credible base exists, spend this lane on the recovery blocker. | One reusable verification foundation or precise remaining prerequisites; run dialog behaves through its real API, not route stubs. |
| 160–210 min | Finish the selected highest-value candidate and its negative cases. Advance security prefix #56–#60 only if time/evidence allow. | After the SuiteDetail handoff, implement one editor slice (#10 then #11/#12 only if coherent and supportable), or independently review the run-modal change. | Correct inputs/expectations survive save/reload; remaining editor capabilities stay open and explicit. |
| 210–240 min | Freeze new scope. Run required exact-head evidence, reconcile checks, create/update narrowly scoped PRs and work records. | Independent full review for each ready candidate; one targeted review only if a fix delta exists. | Exact base/head, commands/jobs, inventories, reviews, unresolved findings and next queue. No rushed merge or false parent closure. |

This is a scheduling budget, not a requirement to make every listed change.
When an early prefix consumes the window, finish fewer pieces completely.
Do not consume the review window to start another feature.

## PR and issue boundaries

| Piece | Existing owner/candidate | Acceptance and independent review focus | Bound |
| --- | --- | --- | --- |
| Regex and test foundation | #31 / PR #48 | Bounded hostile-pattern behavior, aggregate budgets, cancellation and a nonzero test inventory; review reachable current findings rather than repeating historical comments. | 30 min initial diagnosis; one changed hypothesis if repair is needed. |
| Web build and contract repair | #23, #37 / PR #50 | Production build, zero-warning lint, component coverage, numeric/string status, method fields, header DTOs and result parsing. Preserve UI placeholders as unfinished until behavior exists. | 30–45 min validation/targeted repair. |
| Worker boot and contracts | #25 / PR #51 | Actual ASGI startup, readiness, mounted routes, malformed provider output, structured errors and deterministic fixtures. Review the current Azure readiness finding with evidence. | 30–45 min; provider credentials must not be invented. |
| Dependency and scan foundation | #21 / PR #52 | Current transitive advisories, locked restores, existing named CodeQL/dependency jobs and receipt provenance. Do not call August scans current vulnerability evidence. | 20–40 min; host/registry outages route to the healthy runner. |
| Mapping/coverage/integration | #18, #20 / PRs #53, #54, #55 | Keep existing PR splits. Strict saved schemas, fallback/JSONPath errors, meaningful per-component coverage and actual PostgreSQL/API/worker contracts. | Spend remaining foundation budget only while evidence progresses. |
| Cascading Run Suite selection | #9 under existing #6 | Environment → endpoint → mapping; changing parent clears stale child selections; empty/loading/error/retry states; queue submits valid owned IDs; navigate and poll to terminal result. API resource-graph enforcement remains separately owned by #27. | One 45–70 min implementation candidate, 20–30 min evidence/review. |
| Test input authoring | #10 under existing #6 | Messages editable with role/content validation, persist/reload round trip, invalid JSON rejected and errors visible. Extract shared editor seam before modifying parent page. | One 45–60 min candidate after run-modal handoff. |
| Expectations and integrated editing | #11, #12, #4 | Prefer separate expectation component and integration PRs. Do not ship a UI test as useful while it has zero expectations; enforce #41/#44 error semantics. YAML round trip and edit wiring are explicit integration acceptance. | Stretch work only; no claim all eight types will finish in one hour. |
| Security boundary recovery | #29/#26/#27/#28, PRs #56/#57/#59/#60 | Default signing keys, two-tenant access, cross-project resource graph, outbound credentials and SSRF/redirect rules. Preserve separate PRs and deeper security review. | Optional remaining prefix; no deployment before these qualify. |

PRs #65, #66, #70, and #71 remain in their original order after #60. Their
auth admission, polling and browser foundations are useful, but must not be
cherry-picked into a misleadingly green aggregate. The newest observed #71
still contains the run placeholder and empty test creation, so recovering
the full existing stack alone would not finish the application.

## Review and recovery rules

For every candidate, record base/head, immediate parent, effective diff,
closing targets, required check inventory, actual tested SHA (including any
merge-ref SHA), artifact availability and independent reviewer. A clean merge
state or historical successful job is insufficient without relevant receipts.
Restacks require semantic conflict review and refreshed applicable exact-head
evidence. Unchanged patches do not need repeated full reviews.

Use one full independent review, then one targeted fix-delta review; a third
targeted pass requires a credible new P0/P1 introduced by the fix. Dispose each
root cause once as FIX-IN-PR, DEFER-ISSUE, NO-CHANGE or DUPLICATE-STALE. The
author or co-author cannot review their own implementation. Do not replace
executable tests with model approval.

Three failed/superseded strategies exhaust the normal candidate budget.
Ordinary edit/test cycles are not separate strategies. One additional bounded
extension needs independent cause analysis, a new falsifiable hypothesis,
unchanged scope, a finite write set and an explicit time/attempt cap. Carry
consumed time and attempts forward. No unchanged retry on the damaged Docker
disk, no coverage exclusions to achieve a number, and no silent skipped gate.

## Expected end-of-window result

The minimum useful result is a freshly assessed recoverable stack prefix with
complete review dispositions and a verified environment or a precise recovery
prerequisite. With a healthy runner and limited repair, target the foundation
plus the Run Suite candidate; a test-authoring candidate is stretch work.
Report candidates ready for review separately from accepted/merged work.

The final handoff must list accepted work (if merge was authorized), open PRs
and exact SHAs, queued prerequisites, findings with their issue owners,
remaining validation, consumed budgets, and the next single useful action.
No scheduled continuation is created by this plan.
