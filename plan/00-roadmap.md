# CBIX Implementation Plan — Roadmap

Source of truth: `docs/Cross_Border_Instruction_Extraction_Solution_Design.md` (Draft v0.3). This plan does not restate design rationale; it sequences the design into milestones, sprints and stories. Where this document and the design doc appear to disagree, the design doc wins — file an issue against this plan rather than the code.

Sprint cadence: 2-week sprints, numbered sequentially (Sprint 01, Sprint 02, …). No calendar dates are used anywhere in this plan; sprint numbers are the only time axis.

## Milestones

Milestones M0–M3 map 1:1 to design §10's delivery phases.

### M0 — PoC (design §10 Phase 0)

**Sprints:** 01–02 (2 sprints).

**Entry conditions:** none. The specimens (`data/Cross_Border_Trading_Legal_Instruction_{DE,CH}_SPECIMEN.pdf`) are synthetic, so there is no data-governance blocker to starting immediately (design §10, §8).

**Scope:** skeleton MAF workflow against the two specimen documents, calling the Claude API directly.

**Exit criteria** (design §10, verbatim):
- ≥ 98% matrix cell accuracy on the golden set.
- ≥ 95% exact-match on scalar fields.
- Grounding and referential-integrity gates at 100% by construction.
- A written cost-per-document figure from real runs.

### M1 — Pilot (design §10 Phase 1)

**Sprints:** 03–05 (3 sprints).

**Entry condition:** data-governance sign-off for real documents (direct API or Bedrock/Vertex route per design §8). That sign-off must explicitly cover the **provider-side artefact lifecycle** (raised by the S01-05 security review): every run uploads the source PDF to the Anthropic Files API, where it persists until deleted — no TTL, no inventory, and the `file_id` is checkpointed. Before any real document is processed, a deletion/retention policy for those third-party copies must exist and be implemented or consciously waived; design §11's retention question covers the bank's copies, not these.

**Scope:** five pilot jurisdictions spanning layout variety, 100% human review, a real review UI, backfill of those jurisdictions' current versions.

**Exit criteria** (design §10):
- Correction rate per section measured and documented across the pilot set.
- Prompts, model tiering and the normalisation dictionary tuned from captured corrections.
- Sustained correction rate trending toward the < ~2% threshold (design §10 Phase 2) is reported, even if the threshold itself is not yet reached — reaching it is the M1→M2 gate, not an M1 exit requirement, since it needs two consecutive review cycles of real pilot data that may span into M2's start.

### M2 — Scale (design §10 Phase 2) — OUTLINE ONLY

No sprint files exist yet for M2. Story-level detail is deliberately deferred until pilot learnings (M1) are in — tuning corrections, layout-family coverage and actual correction rates should shape the M2 backlog rather than be guessed now.

**Entry condition:** sustained correction rate below ~2% for two consecutive review cycles (design §10 Phase 2), measured by the M1 harness built in Sprint 05.

**Goals:**
- Remaining jurisdictions and historical versions onboarded via the Message Batches API.
- Review moves from 100% to risk-based sampling once the entry condition is met; drift monitoring stays on permanently.
- Matrix-change diff reports generated between versions for Legal sign-off.

**Exit criteria (indicative, to be refined at M2 planning):**
- All remaining jurisdictions onboarded and passing the golden-set gate.
- Risk-based sampling operating with drift monitoring green over a defined observation window.
- Diff-report workflow in production use by Legal.

**Candidate scope (not yet broken into stories):**
- Production work-queue transport: migrate from the SQL-table queue (PoC/pilot) to the existing Kafka estate or RabbitMQ (design §7).
- Full historical-estate backfill via the Message Batches API (Sprint 05 builds the mechanism for the 5 pilot jurisdictions only; M2 extends it to the remaining jurisdictions and full history).
- Risk-based review sampling policy and its rollout, replacing 100% review.
- Matrix version-to-version diff report for Legal sign-off (a by-product of effective-dated storage, design §10).
- Permanent drift-monitoring dashboards and alerting thresholds.
- Anthropic API routing decision for production data: direct API vs Bedrock vs Vertex (design §8, open question).
- Optional: enabling the Claude citations feature on document blocks for platform-anchored page references alongside self-reported snippets (design §5.4 — explicitly optional there, deferred here as it is not required for any M0/M1 exit criterion).
- Optional: cell bounding-box UX improvements to the review UI, contingent on reviewer feedback from M1 (design §11 risk — snippet text-match is accepted as sufficient for M0/M1).

### M3 — Adjacent families (design §10 Phase 3) — OUTLINE ONLY

No sprint files exist yet for M3.

**Entry condition:** M2 exit criteria met; the pipeline has run in production at scale for at least one full review cycle under risk-based sampling.

**Goals:** generalise the pipeline by swapping the section-agent set and target schema onto a new document family. Design §10 names client-level SSI mandates as the natural first candidate.

**Exit criteria (indicative, to be refined at M3 planning):**
- A second document family (e.g. SSI mandates) extracted end-to-end through the same workflow shape (ingest → triage → fan-out section agents → normaliser → validator → persist) with its own schema and golden set.
- Golden-set gate green for the new family at thresholds equivalent to M0's.

**Candidate scope (not yet broken into stories):**
- Family-specific section-agent set and structured-output contracts for SSI mandates (or the chosen adjacent family).
- Family-specific published schema (parallel to design §6, not a reuse of `permission_matrix` etc.).
- Confirmation that `IDocumentContentProvider`, the validator gate framework, checkpointing, HITL and persistence infrastructure are family-agnostic as designed, with any gaps found fixed as infrastructure changes rather than family-specific hacks.

## Sprint index

| Sprint | Milestone | File | Focus |
|---|---|---|---|
| 01 | M0 | `sprint-01-scaffolding-and-provider-abstraction.md` | Solution scaffolding, LLM-agnostic provider abstraction, ingest, minimal topology, triage, DocControl agent |
| 02 | M0 | `sprint-02-extraction-validation-and-eval.md` | Remaining six section agents, fan-out/fan-in, normaliser, validator gates, retry loop, persistence, golden-set eval, M0 exit measurement |
| 03 | M1 | `sprint-03-checkpointing-and-hitl.md` | SQL Server checkpointing, resume-after-crash, HITL port, review queue, correction capture, token budget, OTel |
| 04 | M1 | `sprint-04-review-ui-and-pilot-onboarding.md` | Review UI, normalisation dictionary maintenance flow, five pilot jurisdictions |
| 05 | M1 | `sprint-05-batches-tuning-and-m1-exit.md` | Batches API backfill, correction-rate measurement, drift sampling, prompt/tier tuning, M1 exit review |

## Design-component coverage

Every §5 component of the design is covered by at least one story across Sprints 01–05:

| Design component | Covered in |
|---|---|
| §5.1 Ingestion (hash/dedupe/registry, PDFPig, Files API) | Sprint 01 |
| §5.1 `IDocumentContentProvider` (Claude / generic-vision / text-only profiles) | Sprint 01 |
| §5.2 Workflow topology, hosting | Sprint 01 (minimal), Sprint 02 (full fan-out/fan-in) |
| §5.2 Checkpoint storage (SQL Server) | Sprint 03 |
| §5.2 Work queue (SQL-table, PoC) | **Deferred to Sprint 02** (corrected at Sprint 01 close: no Sprint 01 story delivered a queue or a worker intake loop — the composition and startup probes shipped, but `Worker.cs` remains a placeholder; ownership recorded on S02-01's Done means). Kafka/RabbitMQ migration deferred to M2 |
| §5.3 Triage agent | Sprint 01 |
| §5.4 Section extraction agents (all seven) | Sprint 01 (DocControl), Sprint 02 (remaining six incl. Matrix) |
| §5.4 Matrix agent escalation path | Sprint 02 |
| §5.4 Citations feature (optional) | Deferred to M2 (explicitly optional in design) |
| §5.5 Normalisation agent + dictionary | Sprint 02 (build), Sprint 04 (maintenance flow), Sprint 05 (tuning) |
| §5.6 Deterministic validator gates (all) | Sprint 02 |
| §5.6 Targeted retry loop | Sprint 02 |
| §5.7 Human-in-the-loop (request/response port, queue, UI, correction capture) | Sprint 03 (port, queue, capture), Sprint 04 (UI) |
| §5.8 Persistence (staging, publish, effective dating, provenance) | Sprint 02 |
| §5.9 Evaluation harness (four metrics, CI gate) | Sprint 02 |
| §5.9 Production drift sampling | Sprint 05 |
| §5.9 Batches API backfill | Sprint 05 (pilot jurisdictions); full-estate backfill deferred to M2 |
| §7 Token budget middleware | Sprint 03 |
| §7 / §8 OTel traces and metrics | Sprint 03 |
| §7 Secrets manager for API key | Sprint 01 |
| §7 Transport migration to Kafka/RabbitMQ | Deferred to M2 |
| §8 Risk-based sampling, diff reports | Deferred to M2 |
| §10 Phase 3 adjacent families | Deferred to M3 |

Nothing is silently dropped: every deferral above is named and justified in the M2/M3 outlines.

## Working agreements

**BDD is mandatory for all development.** Every story starts with a failing executable scenario before any implementation code is written. Scenarios are written in Gherkin `.feature` files and executed with **Reqnroll** bound to **xUnit** for .NET. A story is not "in progress" until its `.feature` file exists and its scenarios fail for the right reason (missing implementation, not a compile error).

**Definition of Done template** (applies to every story unless its own `Done means` section adds to it):
1. Failing executable scenario written first (Reqnroll `.feature` + step bindings), confirmed failing for the right reason.
2. Implementation makes the scenario green.
3. Refactor with the scenario staying green.
4. Build, lint and format run clean — zero warnings.
5. Documentation current (this plan, `CLAUDE.md`, and any XML doc comments on public contracts affected by the story).
6. Full unit suite green, not just the story's own scenario.

**Story state tracking.** Each story heading carries a markdown checkbox: `### [ ] S<sprint>-<nn> — <title>`. Check it (`[x]`) only when the Definition of Done above is fully met, including a green full-suite run. Partial completion is not reflected by editing the checkbox; use PR/commit references for in-progress status instead.

**Story sizing.** Every story is sized S or M. A story that would be L must be split before it enters a sprint — each story targets exactly one observable behavior, so it can be demonstrated with one (or a small number of tightly related) Gherkin scenario(s).

**Dependency direction.** Stories may depend on earlier stories in the same sprint or on stories from earlier sprints. No story depends on a later sprint's work.
