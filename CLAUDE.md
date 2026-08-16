# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository state

**Pre-implementation.** There is no source code yet — no build, lint, or test commands exist. The repository contains the solution design and the golden-set seed data. Update this file with real commands once the .NET solution is scaffolded.

- `docs/Cross_Border_Instruction_Extraction_Solution_Design.md` — the authoritative design (Draft v0.2). Read it before writing any code; every architectural decision below is elaborated there with rationale.
- `data/Cross_Border_Trading_Legal_Instruction_{DE,CH}_SPECIMEN.pdf` — synthetic specimen input documents (fictional "Contoso Bank"; no real data, so no data-governance restrictions).
- `data/cbti_country_ground_truth.json` — golden set v0: the expected extraction for both specimens, pre-flattened to relational shape. DE has a 10 products × 4 MiFID II categories matrix (40 cells), CH has 10 × 3 FinSA categories (30 cells). The two categorisation schemes differ deliberately to exercise taxonomy normalisation.

## What CBIX is

A pipeline that converts per-country Cross-Border Trading Legal Instruction PDFs into validated, audit-grade relational data. Planned stack: **.NET worker** using **Microsoft Agent Framework (MAF) Workflows** for orchestration, the **Anthropic Claude API** (via the official C# SDK's `Microsoft.Extensions.AI` `IChatClient` integration; community `Anthropic.SDK` is the fallback) for extraction, **PDFPig** for a local text layer, and **SQL Server** for checkpoints, staging, and the published schema. Hard constraint: **no Azure services**.

## Governing principle

**Agents propose, code disposes.** LLM agents never touch the database and never decide whether output is accepted. They produce typed candidates; deterministic code validates, gates, and persists. Anything that can be checked in plain code (schema conformance, referential integrity, completeness counts, grounding) must be checked in plain code, never delegated to a model.

## Architecture (see design doc §4–§5 for detail)

One MAF workflow run per document, checkpointed to SQL Server at each superstep so runs resume without re-paying LLM calls:

1. **Ingest (code)** — content-hash dedupe, registry record, PDFPig text layer, single upload to the Claude Files API (`file_id` + `cache_control`, so all later agents hit prompt cache).
2. **Triage agent** (Haiku tier) — returns a `DocumentProfile`; routes unknown documents to review instead of guessing.
3. **Seven section agents in parallel fan-out** — DocControl, Entities, Matrix, Conditions, Marketing, Tax, VersionHistory. Each has its own small structured-output schema (deliberately decomposed: better schema adherence, independent retry, per-section confidence). All run Haiku except **Matrix (Sonnet)**, the hard case: Claude's native PDF mode reads the table visually and must emit exactly products × categories cells. Tiering is decided by the eval harness, not dogma.
4. **Normaliser agent** — dictionary-first (agent only for unmapped values); maps raw client categories (MiFID II / FinSA) onto the canonical taxonomy; emits `UNMAPPED` → review rather than guessing.
5. **Validator (code, sole authority to persist)** — gates in order: schema/enum conformance; **grounding** (every `SourceSnippet` must appear verbatim in the PDFPig text layer — string containment); referential integrity (matrix condition refs resolve to extracted conditions); completeness (full cell count, monotonic version history, date ordering); supersession-chain checks. Failures produce a structured `ValidationReport`; the retry edge re-invokes **only the failing agent** with the report in context, max 2 attempts, then human review.
6. **Human review** — via MAF request/response ports (checkpointed pause). Every correction is appended to the golden set — review is the improvement flywheel, not just a safety net.
7. **Persist (code)** — staging → transactional publish, effective-dated on `(doc_ref, doc_version)`; superseded versions get closed validity windows, never deleted.

Every extracted scalar is wrapped in the provenance envelope `ExtractedField<T>(Value, SourcePage, SourceSnippet, Confidence)` and lands in `field_provenance` alongside model id and prompt version. The audit bar (design doc §8): any published value must answer in one query — which document/page, verbatim source text, which model/prompt produced it, confidence, and reviewer.

## Extraction prompting rules (uniform across agents)

Extract, never interpret. Copy snippets verbatim. Return null for absent fields — never invent. Use only the supplied document. Logical page numbers as shown in a PDF viewer.

## Quality gate

The golden set (`data/cbti_country_ground_truth.json`, growing with every human correction) backs a CI regression harness: field-level exact-match, **matrix cell accuracy (headline metric)**, condition-linkage F1, normalisation accuracy. No prompt, model version, or configuration change ships without a green golden-set run. PoC exit criteria: ≥ 98% matrix cell accuracy, ≥ 95% scalar exact-match, grounding and referential-integrity at 100%.

## Key schema facts

Published tables (T-SQL sketch in design doc §6): `country_documents` (PK `doc_ref, doc_version`, validity window), `permission_matrix` (status codes constrained to `P | PR | RS | NP | N/A`), `conditions`, `matrix_conditions` (cell↔footnote many-to-many), `field_provenance`. Operational: `document_registry`, `extraction_runs`, `review_queue`, `category_mappings` (normalisation dictionary), `golden_set`.

## Engineering cautions from the design (§11)

- MAF and the official Anthropic C# SDK are both young: pin package versions and model strings; wrap framework/provider types behind thin interfaces.
- Don't trust model-reported confidence at face value — thresholds are calibrated against observed correction rates.
- No cell bounding boxes from PDF mode: provenance is page + verbatim snippet, and the review UI locates sources by snippet text-match.
