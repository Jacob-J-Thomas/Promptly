# AIDLC delivery conventions for Promptly

This document adapts the evidence-first AIDLC approach to Promptly's current
monorepo. It is a repository contract, not proof that the missing CI, behavior
tests, coverage collection, or review automation already exists. The audited
main baseline was `849a00b113ae694f3e8997b212e7e8786378ad0c`; it may have
historical workflow or pull-request evidence elsewhere, so the current state
must be rechecked with [`scripts/aidlc_preflight.py`](../scripts/aidlc_preflight.py)
before each delivery session.

## The delivery graph

Promptly tracks admitted work as:

```text
Campaign -> Phase -> Unit of Work (UOW) -> Bolt -> Pull Request
```

The graph has a practical meaning:

| Level | Meaning | Completion evidence |
| --- | --- | --- |
| Campaign | A product or quality outcome with a bounded portfolio | Every admitted Phase has its own evidence and disposition |
| Phase | A coherent outcome that can be planned and accepted separately | Its contract, gates, review, and child dispositions are complete |
| UOW | A related slice owned by one delivery thread | Its Bolts are complete, deferred, or explicitly rejected |
| Bolt | One smallest reviewable implementation outcome | Exact-head verification, bounded review, and acceptance for that outcome |
| Finding | A discovered defect or debt while triaging | Reproduction, deduplication, severity, and explicit admission or deferral |

Findings remain parentless during triage. A finding becomes active work only
after native parentage, ownership, acceptance evidence, and non-goals are
recorded. A pull request normally closes one Bolt. If one PR must cover more
than one Bolt, its contract names every Bolt, the shared evidence, and the
closure order.

Preserve the owner and meaning of existing work. A new finding does not replace
an open PR or branch merely because its code is inconvenient to review. Use a
handoff or a clearly recorded transfer when ownership really changes. Keep
existing dirty worktrees, failed receipts, stale evidence, and historical issue
meaning available for recovery.

Use the lifecycle labels and status values that the live repository defines;
proposed names such as `finding`, `bug`, `queued`, `review`, or `blocked` must
be checked against that inventory before any issue mutation. Labels and native
parent relationships must agree with the actual state. `blocked` names a human
or external action, its owner, and the exit evidence. A failing check, routine
retry, or merge ordering constraint is not a human block. Deduplicate findings
by causal mechanism and keep one issue for one coherent problem.

## The work contract

Before changing code or delivery policy, record these fields in the issue or
work ledger:

- observable user or system outcome;
- Campaign, Phase, UOW, Bolt, existing owner, and immediate parent;
- in-scope and explicitly out-of-scope surfaces;
- changed systems and protected product invariants;
- expected QA reproduction or acceptance scenarios;
- focused checks, authoritative gates, environment requirements, and coverage
  evidence;
- independent full-review and fix-delta-review plan;
- known debt, dependent work, attempt and time limits, and recovery boundary;
- current authority, intended mutations, and merge intent.

The contract is finite. New scope becomes a deduplicated Finding and waits for
triage or a separately admitted Bolt. A plan for the next few hours should
sequence these contracts and name dependencies; it does not authorize future
campaigns or merge PRs by itself.

## User-like QA and finding intake

Start with the actual product journey and expand to failure paths. For Promptly
that means exercising, where the environment allows:

1. registration, login, token expiry, refresh, and a protected deep link;
2. project creation, project isolation, and empty-state behavior;
3. environment and endpoint setup, validation, encrypted headers, and an
   unreachable target;
4. mapping proposal, invalid JSONPath, fallback mapping, preview, save, and
   deterministic reuse during a run;
5. suite and test authoring, YAML import/export, empty or malformed
   expectations, and edit/delete behavior;
6. queued, running, completed, failed, and duplicate/concurrent runs, worker
   timeout/error paths, raw-response limits, and history/detail views;
7. SDK and CLI authentication, non-success responses, polling timeouts, and
   serialization compatibility;
8. browser refresh, navigation, loading/error states, accessibility basics, and
   API responses for malformed, unauthenticated, cross-tenant, and oversized
   inputs; and
9. the Docker Compose startup, migration, health, worker, database, and web
   interactions when Docker is available.

For every finding, retain the exact reproduction, expected and actual
behavior, current commit SHA, environment and tool version, affected
component, severity, and a short evidence link or artifact path. Distinguish a
product defect from an unavailable environment. Do not turn a failed setup or
missing dependency into an invented application result.

Deduplicate by causal mechanism before creating or updating an issue. Link
related symptoms to the root finding, include regression risk, and state the
smallest acceptable repair. A review observation without a reachable defect is
recorded as `NO-CHANGE` or deferred debt rather than automatically expanding
the active Bolt.

## Execution loop

Each Bolt follows this sequence:

1. Refresh live repository and work ownership, then restate the contract.
2. Run read-only preflight and capture the baseline inventory. Missing tests,
   workflows, receipts, or coverage remain visible as `MISSING`.
3. Run user-like QA and static inspection. Triage and deduplicate findings.
4. Admit only the bounded findings selected for the current Bolt. Preserve
   existing owners and parentage.
5. Implement the smallest coherent change. Keep migrations, public contract
   changes, security changes, and unrelated UX work separate when they can be
   reviewed independently.
6. Run focused checks, then the complete applicable authoritative gate set on
   the exact head. Record command/job identity, input SHA, environment,
   duration, inventory, receipt, and result.
7. Request one independent full review. Repair reachable P0/P1/P2 blockers in
   the Bolt, or classify and link a valid nonblocking finding.
8. If the full review produces an admitted fix delta, request one independent
   targeted review of that delta. Use a third pass only for a credible new
   P0/P1 caused by that repair.
9. Revalidate head/base, ancestry, checks, review receipts, parentage, and
   invariants. Accept the Bolt only when no required evidence is missing and
   no unresolved merge blocker remains.
10. Leave the PR open when the active delivery contract does not delegate merge
    authority. When that contract covers an in-scope exact-green PR, merge only
    after its final head is revalidated; then revalidate the merge commit/tree
    and close only the supported Bolt boundary.

Any restack, conflict repair, or material fix invalidates evidence for the
changed effective patch. Repeat the gates and review required by the delta and
retain the old failure evidence for the ledger.

## Review and disposition ledger

The PR should carry a compact ledger with:

```text
reviewed base: <exact SHA>
reviewed head: <exact SHA>
reviewer: <independent identity>
full review: <request and receipt>
targeted delta review: <range and receipt>
findings:
  - root cause: <one causal mechanism>
    disposition: FIX-IN-PR | DEFER-ISSUE | NO-CHANGE | DUPLICATE-STALE
    severity: P0 | P1 | P2 | P3
    evidence: <reachable path or artifact>
    residual uncertainty: <explicit statement>
```

The implementation author cannot certify their own full review. Review text is
evidence that must be reconciled with executable gates; it is not a gate
substitute. Do not repeat unchanged reviews or inflate severity to justify a
new pass.

## Recovery and stopping

Classify a failed attempt as code, contract, test, infrastructure, or access
failure. Preserve its output, exact SHA, and consumed budget. A routine retry
does not create a new Bolt or a new issue. Before replacing a candidate, record
an independent root-cause assessment, a changed falsifiable hypothesis, an
unchanged scope/invariant statement, a finite write set, named verifier and
reviewer, attempt/time cap, and terminal exit criterion.

Stop the candidate when the bound is exhausted or a required owner/external
action is genuinely unavailable. Record `queued`, `blocked`, or `failed` for
the specific contract and keep the broader Campaign available for another
admitted Bolt. Never hide a red or absent gate by calling a quick check
acceptance.

## Promptly-specific initial gaps

The repository baseline currently has application source and a Compose
definition, but it has no committed behavior-test projects, browser suite,
coverage policy/receipt, `.github/workflows` gate, or full verification script.
The initial pipeline therefore treats local build/lint, static inspection, and
manual QA as diagnostic evidence. Work to add isolated behavior tests,
meaningful per-component coverage, dependency and security checks, exact-head
CI receipts, and a full-stack/browser acceptance path must be scheduled as
separate Bolts. Until those are present, a green compilation cannot close a
production component or certify the application.
