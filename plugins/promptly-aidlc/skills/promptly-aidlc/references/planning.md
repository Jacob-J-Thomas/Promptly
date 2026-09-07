# Bounded Promptly planning

Use this reference after the audit and issue deduplication are complete. Produce a plan that a human can inspect and a delivery owner can execute in the next few hours. The plan is a deliverable; it does not start implementation, create PRs, merge, or schedule later runs.

## Plan entry

For each proposed unit, write:

```text
Priority and user outcome:
Existing issue/PR or new finding:
Native parent (Campaign / Phase / UOW / Bolt), if admitted:
Exact source and surface boundary:
Dependencies and owner:
Acceptance checks and evidence receipt:
Independent review plan:
Estimated time box and stop condition:
Known risks, deferred debt, and rollback/recovery:
```

Rank by user value and unblockability, then by security/data risk. Keep a PR coherent: a worker boot contract, frontend compilation/contract repair, test-case authoring, run configuration, and tenant/security hardening usually require different review and acceptance evidence. Combine changes only when they share a causal root and can be verified together. Preserve the existing draft stack and avoid duplicate work until exact diff/ownership is understood.

## Suggested time-box shape

Adapt this sequence to measured feasibility and the current issue/PR graph:

1. **0-15 minutes — feasibility and truth**: confirm exact SHA, draft stack, dirty worktrees, free disk, Docker I/O, dependency caches, credentials, browser tooling, workflows, and tests.
2. **15-45 minutes — audit closure**: replay the documented core journeys, capture deterministic findings, search/deduplicate issues, and separate static leads, route fixtures, infrastructure blockers, and real product defects.
3. **45-90 minutes — boot and contract unblock**: if admitted, make the worker/API health path real and observable, then verify Compose and HTTP contracts. Keep this separate from UI work if the root cause differs.
4. **Next 60-120 minutes — highest-value user flow**: select one coherent slice such as scenario test editing or run configuration, with browser, API, persistence, and error/latency acceptance. Split an editor and run modal when their contracts differ.
5. **Final window — evidence and review**: run exact-head focused/full gates that are feasible, obtain independent bounded review, update findings and the next queue, and leave PRs open unless separately authorized.

Every entry needs a terminal condition for unavailable infrastructure. If end-to-end boot remains blocked by Docker or host capacity, do not claim product acceptance; use source/API contract checks and produce a recovery prerequisite with the captured diagnostics.

## Completion shape

The plan should state what can be delivered in the time box, what remains queued or blocked, the order of PRs, and the checks/reviews that make each PR independently acceptable. Do not call a placeholder removed, a fixture green, or a draft PR complete without behavior and exact-head evidence.
