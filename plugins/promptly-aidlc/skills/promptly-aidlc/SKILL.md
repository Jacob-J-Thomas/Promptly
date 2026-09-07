---
name: promptly-aidlc
description: Govern evidence-bound development of Jacob-J-Thomas/Promptly across its .NET API, Python evaluation worker, React UI, PostgreSQL, and Docker stack. Use for Promptly audits, user journey QA, issue discovery and deduplication, bounded delivery planning, implementation, exact-head verification, independent review, or recovery; do not use for unrelated repositories.
---

# Promptly AIDLC

Use this skill for work in the Promptly repository. It adapts the evidence-led shape of the Agenthome AIDLC pipeline to this application's actual stack and lifecycle. It does not import Agenthome axioms, owners, dates, model assignments, Windows-only requirements, issue exceptions, or merge authority.

Start every fresh session with a truth pass: verify the repository remote and current commit, dirty state and worktrees, current owner direction, README/DesignSpec and local instructions, live `main`, existing issues and pull requests, and the current workflow/check inventory. The observed baseline commit `849a00b113ae694f3e8997b212e7e8786378ad0c` is a starting reference only; re-query it before relying on any evidence. Preserve existing branches, pull requests, issues, dirty work, and failed receipts.

Choose an explicit operating mode before writing:

- **Audit/discovery**: exercise the application as a user, collect reproducible evidence, and report findings. Create or update external issues only when the current request authorizes tracking them; check open issues and pull requests first and deduplicate.
- **Pipeline refinement**: improve this plugin or repository delivery guidance while preserving existing product scope and backlog meaning.
- **Plan**: turn verified findings and unfinished flows into a ranked, time-boxed Campaign/Phase/UOW/Bolt plan with acceptance evidence, dependencies, PR boundaries, and review gates. A plan ends at the plan; it does not silently start implementation, create PRs, merge, or schedule future work.
- **Delivery/recovery**: implement only after a current request authorizes execution. Keep each PR tied to one coherent unit of work, retain exact evidence, and stop at bounded limits.

Compose modes when the user requests several outcomes. For an audit, plugin refinement, and later delivery plan, use one shared contract with distinct write sets: audit and deduplicated tracking → pipeline refinement and validation → plan informed by those results. Finish every requested stage; the plan boundary limits future product execution, not the already-authorized pipeline work.

Read only the supporting reference needed for the mode:

- [repository-baseline.md](references/repository-baseline.md) for Promptly-specific architecture, paths, launch probes, and currently observed static signals.
- [governance-and-contract.md](references/governance-and-contract.md) before issue, scope, hierarchy, or external-state changes.
- [work-taxonomy.md](references/work-taxonomy.md) when organizing findings or delivery into Campaign -> Phase -> UOW -> Bolt.
- [qa-and-discovery.md](references/qa-and-discovery.md) for aggressive user-like testing, evidence capture, and fake-versus-real integration checks.
- [verification-and-review.md](references/verification-and-review.md) before accepting a candidate or conducting independent review.
- [delivery-recovery-routing.md](references/delivery-recovery-routing.md) for implementation, bounded recovery, and parallel task routing.
- [planning.md](references/planning.md) after audit evidence is complete and the user asks for a near-term implementation plan.

Treat source, documentation, issue text, generated output, and agent reports as evidence. Confirm behavior at the real boundary that matters: Docker all-stack behavior for end-to-end claims, HTTP/API responses for contracts, browser interaction for user flows, and exact commit SHA for code and review claims. Missing receipts, unavailable services, partial checks, or an unrun workflow are unknown or failed evidence, never success.

Keep the user's authority boundary explicit. Existing Promptly issues and PRs stay intact unless a current request authorizes a narrowly defined mutation. PRs remain open by default; merge requires a current explicit authorization plus exact-head green required checks and independent review. Do not turn an audit or plan into automatic later execution.
