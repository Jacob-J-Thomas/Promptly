## Outcome

Describe the observable user or system outcome and the Promptly journey it
changes.

## Delivery contract

- Campaign:
- Phase:
- UOW:
- Bolt:
- Existing owner / handoff:
- Intended base SHA:
- Current head SHA:
- Immediate stack parent:
- Changed systems:
- Protected invariants:

For the initial governance bootstrap, record bootstrap work item #73 and
related audit #47 (after a live recheck). This bootstrap exception may precede
native parentage; new product and QA work requires native parentage when
explicitly admitted.

## Scope

### In scope

-

### Non-goals

-

### Findings and follow-ups

List the deduplicated finding(s) this Bolt addresses. Link deferred or
duplicate findings and explain their disposition.

## User-like QA

| Scenario | Expected | Actual | Environment / evidence | Result |
| --- | --- | --- | --- | --- |
|  |  |  |  | PASS / FAIL / MISSING |

Include relevant happy path, malformed input, authentication/tenant boundary,
failure/retry, and browser or Compose scenarios. Redact credentials and
personal data.

## Verification ledger

| Gate | Exact command or hosted job | Input SHA | Environment | Inventory / receipt | Result |
| --- | --- | --- | --- | --- | --- |
| Preflight (diagnostic) |  |  |  |  |  |
| Focused checks |  |  |  |  |  |
| Component behavior |  |  |  |  |  |
| Full-stack / browser |  |  |  |  |  |
| Security / dependency |  |  |  |  |  |
| Coverage (>=90% per production component) |  |  |  |  |  |

Mark unavailable, skipped, stale, timed out, or partial evidence explicitly.
A local quick check never substitutes for a required exact-head gate.

## Independent review ledger

- Full review: reviewer, request/receipt, exact base/head:
- Targeted fix-delta review (only when a fix delta exists): reviewer, range/receipt, exact head:
- P0/P1 blockers:
- P2/P3 findings and linked follow-ups:
- `FIX-IN-PR` root causes:
- `DEFER-ISSUE` root causes:
- `NO-CHANGE` or `DUPLICATE-STALE` root causes and evidence:
- Residual uncertainty:

The implementation author or co-author must not be the independent reviewer.

## Merge and closure intent

State whether the active delivery contract delegates merge authority for this
in-scope exact-green PR. When it does not, leave the PR open. If a delegated
merge occurs, record the exact match SHA, method, merge parents/tree, and
post-merge `main` revalidation before closing the Bolt.
