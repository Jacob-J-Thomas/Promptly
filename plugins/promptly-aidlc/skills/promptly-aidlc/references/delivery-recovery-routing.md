# Delivery, bounded recovery, and routing

Implement the smallest coherent outcome that closes one admitted Bolt or an explicitly contracted shared unit. Keep API/domain/persistence, worker runtime, frontend behavior, security, and documentation separate when their acceptance evidence or rollback differs. Preserve the current stack, existing work, and documented product boundary.

## Delivery loop

1. Restate the contract, invariants, non-goals, finite write set, dependency order, and acceptance evidence.
2. Inspect the reachable source and existing issue/PR work before editing. Surface documentation/code contradictions early.
3. Work in an isolated branch/worktree when another checkout is dirty or another candidate owns the seam. Do not reset, overwrite, or rebase someone else's work without an explicit handoff.
4. Run focused checks while developing, then the complete required gate inventory on the exact head. Keep the PR description tied to the user-visible outcome and list known gaps.
5. Obtain independent review, classify each root cause once, repair only admitted blockers, and rerun the evidence required by the changed patch.
6. Leave the pull request open unless current explicit user authority includes merge. Revalidate head, base, checks, review, hierarchy, and intended successor order before any closure.

## Bounded recovery

Routine errors, failed reviews, context limits, and temporary runner problems are delivery states. Preserve their evidence and classify the specific cause. Count materially distinct candidate strategies, not ordinary edit/test cycles; use three failed or superseded attempts as the normal ceiling. Before a replacement or further retry after that ceiling, record one bounded recovery extension containing:

- an independent causal assessment of the exact failure;
- a changed, falsifiable hypothesis for the next action;
- confirmation that product scope and invariants are unchanged;
- a named finite write set, verifier, reviewer, attempt cap, time cap, and exit criterion;
- retained failed evidence, budget consumed, and validation/review plan.

Reject unchanged retries under a new name, gate waivers, hidden red checks, circular prerequisites, self-renewing extensions, and scope expansion. Exit with accepted evidence, a new finite alternative contract, `QUEUED` for a technical prerequisite, `BLOCKED` for a real human boundary with exit evidence, or `FAILED` for the specific candidate. Do not convert candidate failure into a global stop unless the current owner decides it.

## Parallel routing

Parallelize only independent bounded work that the current request authorizes. A primary owner keeps the contract, issue/PR mutations, integration, acceptance, and global stop decision. Useful routes include:

- repository/issue inventory and duplicate search;
- browser journey QA and accessibility/console evidence;
- API/domain/tenant/security inspection;
- worker/container health and integration probes;
- focused implementation of separate Bolts;
- independent exact-head review.

Give each route a narrow objective, files or environment in scope, read/write boundary, evidence format, time/attempt cap, and handoff SHA. Read-only probes may run in parallel; writes touching the same files, issue, branch, database fixture, or external service are sequential or isolated. Do not assign the implementation author to review its own or a co-author's patch. Do not import fixed role or model ownership from another pipeline; select available agents by the actual complexity and preserve one accountable integrator.
