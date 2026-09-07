# Promptly AIDLC targeted fix-delta review

Date: 2026-09-07

Scope: targeted review of the six mechanisms identified in `behavior.md`. This pass does not repeat the full behavioral review. No source, GitHub, issue, PR, branch, or product writes were made.

## Disposition

**ACCEPTED — no new P0/P1 finding and no additional repair required for this delta.** The current copy closes the six reviewed causal paths while preserving the product boundary, existing backlog/PR ownership, exact-head evidence, bounded recovery, and user authority rules.

- **F1, composite modes — closed.** `plugins/promptly-aidlc/skills/promptly-aidlc/SKILL.md:19` explicitly sequences audit and deduplicated tracking → pipeline refinement and validation → plan, with one shared contract and distinct write sets. The plan remains a future-execution boundary; already-authorized pipeline work is not silently dropped.
- **F2, stale review after restack — closed.** `plugins/promptly-aidlc/skills/promptly-aidlc/references/verification-and-review.md:33` binds the final receipt to the exact current head/base, limits older receipts to their original patch, and requires review of the changed semantics/conflict delta. A patch-equivalent restack can cumulatively rebind with explicit equivalence evidence without forcing an unnecessary full review.
- **F3, empty required-check set — closed.** `plugins/promptly-aidlc/skills/promptly-aidlc/references/verification-and-review.md:16` makes applicability an explicit contract/workflow/policy inventory, states that an empty ruleset is not green, and fails acceptance on unknown applicability, missing gates, or unavailable receipts. `:37` also requires branch protection for merge.
- **F4, bare/commented review — closed.** `plugins/promptly-aidlc/skills/promptly-aidlc/references/verification-and-review.md:33` requires every finding disposition, leaves no unresolved P0/P1 or declared blocker, and rejects a bare `COMMENTED` review, author reply, or silence as approval evidence.
- **F5, unsafe Compose probe — closed.** `plugins/promptly-aidlc/skills/promptly-aidlc/references/repository-baseline.md:33-56` requires the isolated project, generated override, URLs, and file preflight; constructs one `qa_compose` array with `-p` and both Compose files; refuses the baseline stack; and preserves sanitized evidence before scoped cleanup.
- **F6, evidence disclosure — closed.** `plugins/promptly-aidlc/skills/promptly-aidlc/references/repository-baseline.md:56` and `plugins/promptly-aidlc/skills/promptly-aidlc/references/qa-and-discovery.md:51` require sanitized logs/artifacts, redaction before retention or publication, and final issue/PR evidence review.

## Authority and scope check

`plugins/promptly-aidlc/skills/promptly-aidlc/references/governance-and-contract.md:38-40` still requires current exact-head gates and independent review, while recognizing standing explicit in-scope merge delegation without repeated per-PR approval. The delta adds no inherited Agenthome owners/models/dates, no unrequested license metadata, no backlog reparenting, no gate waiver, and no authority to merge unrelated work.

## Residual uncertainty

This disposition is against the current skill text only. A live run must still prove the generated Compose override, applicable-gate inventory, exact PR head/base, cumulative review equivalence (where used), and sanitized receipts. Those are acceptance obligations, not reasons to reopen the unchanged full review.
