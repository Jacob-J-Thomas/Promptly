# Promptly work taxonomy

Use the smallest native graph that explains the work:

```text
Campaign -> Phase -> UOW -> Bolt

Finding (parentless until triage)
```

Every admitted Phase, UOW, and Bolt has one immediate parent. A Bolt is a leaf and should describe a reviewable product or delivery outcome. A finding is an observed problem or debt item; it does not become implementation work until its scope, parent, and acceptance evidence are explicitly triaged.

## Choosing a level

- **Campaign**: a product outcome with several related phases, such as making the documented onboarding-to-results journey reliable.
- **Phase**: a bounded capability or release slice, such as worker runtime, test authoring, or run execution.
- **UOW**: a cohesive implementation or repair area owned by one delivery stream.
- **Bolt**: one small change that can have a clear diff, focused checks, and an independent review.
- **Finding**: a concrete reproducible defect, unfinished flow, quality gap, or risk. Keep it parentless until triage decides whether it is duplicate, deferred debt, or admitted work.

Do not create a new hierarchy to avoid reading existing issue parentage. Refresh the current GitHub issue/PR graph first, preserve useful historical meaning, and reparent only with explicit authority. If the repository has no established labels or hierarchy fields, use a clearly documented proposed graph in the plan before mutating external state.

## Lifecycle and status

Use the repository's live status vocabulary. `Queued` means a technical prerequisite or intentionally ordered delivery item. `Blocked` means useful work is waiting on a named human action or external dependency with exit evidence; a failed check, normal retry, or desired merge order is not blocked. A closed parent with active descendants, a closed item with active status, a Bolt with children, or a PR closing a non-Bolt is a graph defect to record and repair deliberately.

One active candidate normally serves one Bolt. A shared PR is permitted only when the contract names all included Bolts, their owner, exact acceptance evidence, and closure order. Do not create replacement PRs merely because a candidate failed; retain the candidate and evidence, then choose a bounded repair or a new strategy with a causal explanation.

## Planning and closure

Each Bolt plan entry should state the user-visible result, source boundaries, dependencies, exact checks, review depth, likely risk, and expected PR size. Keep security, tenant isolation, worker/runtime wiring, frontend behavior, persistence, and documentation as separate units when their acceptance evidence differs. Close a Bolt only on its own evidence; assess UOW, Phase, and Campaign closure separately. Do not mark an unimplemented feature complete to clear a parent or convert deferred work into a false no-op.
