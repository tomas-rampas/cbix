# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository state

**In implementation (Sprint 01).** The .NET solution is scaffolded: `Cbix.sln` at the repo root with `src/Cbix.Core` (contracts/executors — `Cbix.Core.Documents` holds the `IDocumentContentProvider` port, `Cbix.Core.Ingest` the ingest/dedupe/text-layer services, `Cbix.Core.Secrets` the secret-resolver port; Core references only neutral libraries — `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `UglyToad.PdfPig` — enforced by `CoreAssemblyNeutralityTests`' allowlist), `src/Cbix.Providers.Anthropic` (the **sole** assembly referencing `Microsoft.Agents.AI.Anthropic` and the Anthropic SDK, both exact-pinned — MAF 1.17.0 with the prerelease Anthropic integration; containment is test-enforced in both directions), `src/Cbix.Worker` (worker host), `tests/Cbix.UnitTests` (xUnit) and `tests/Cbix.Bdd` (Reqnroll 3.3.4 on xUnit v2 — `.feature` files in `Features/`, step bindings in `Steps/`, config in `reqnroll.json`; undefined steps hard-fail). SDK pinned via `global.json` (10.0.110, rollForward patch). Shared build properties (net10.0, nullable, `TreatWarningsAsErrors`, `AnalysisModeSecurity=All`) live only in `Directory.Build.props`; package versions are centralised in `Directory.Packages.props` (CPM) with per-project `packages.lock.json` committed, and restore is locked to nuget.org via `nuget.config`.

Commands (run from the repo root):

```
dotnet build Cbix.sln                      # must end 0 Warning(s), 0 Error(s) — warnings are errors
dotnet test  Cbix.sln                      # runs both test projects (unit + BDD)
dotnet format Cbix.sln --verify-no-changes # whitespace gate; run before committing
```

Style rules are enforced by the **build**, not the formatter: `EnforceCodeStyleInBuild=true` plus rules elevated to `warning` in `.editorconfig` become hard failures under warnings-as-errors. `dotnet format --verify-no-changes` only checks whitespace.

Composition root, refined by S01-17: **workflow-graph composition** lives in `Cbix.Core` (lands with S01-13) so tests — including S01-09's stub-client agnosticism run — exercise the real composition without the executable. **Provider selection and credential wiring** live in the host (`Cbix.Worker.CbixWorkerHostExtensions.AddCbixWorker`), because registering a concrete provider means naming its adapter, which Core's containment tests forbid — which deployment uses which provider is a host decision by design. `src/Cbix.Worker` stays a thin `Program.cs` that calls the extension and runs.

Secrets (e.g. `ANTHROPIC_API_KEY`) come from user-secrets or environment variables only — never `launchSettings.json`, `appsettings*.json`, or any other tracked file. `**/Properties/launchSettings.json` is gitignored for exactly this reason.

- `docs/Cross_Border_Instruction_Extraction_Solution_Design.md` — the authoritative design (Draft v0.3). Read it before writing any code; every architectural decision below is elaborated there with rationale.
- `plan/` — the implementation plan: `00-roadmap.md` (milestones M0–M3, sprint index, design-component coverage table, working agreements) plus story files `sprint-01…05`. **BDD is mandatory**: every story starts from a failing Reqnroll/xUnit Gherkin scenario; story checkboxes (`### [ ] Sxx-nn`) are ticked only when the roadmap's Definition of Done is fully met. Keep this plan current as stories complete.
- `data/Cross_Border_Trading_Legal_Instruction_{DE,CH}_SPECIMEN.pdf` — synthetic specimen input documents (fictional "Contoso Bank"; no real data, so no data-governance restrictions).
- `data/cbti_country_ground_truth.json` — golden set v0: the expected extraction for both specimens, pre-flattened to relational shape. DE has a 10 products × 4 MiFID II categories matrix (40 cells), CH has 10 × 3 FinSA categories (30 cells). The two categorisation schemes differ deliberately to exercise taxonomy normalisation.

## What CBIX is

A pipeline that converts per-country Cross-Border Trading Legal Instruction PDFs into validated, audit-grade relational data. Planned stack: **.NET worker** using **Microsoft Agent Framework (MAF) Workflows** for orchestration, the **Anthropic Claude API** via MAF's first-party provider integration (`Microsoft.Agents.AI.Anthropic`, prerelease — `AnthropicClient.AsAIAgent(...)`; no direct Anthropic SDK usage), **PDFPig** for a local text layer, and **SQL Server** for checkpoints, staging, and the published schema. Hard constraints: **no Azure services** (incl. no Foundry), and **LLM-agnosticism** — Anthropic types stay inside one provider adapter; everything else depends on `AIAgent`/`IChatClient` plus a capability profile (document presentation behind the `IDocumentContentProvider` port: Claude native-PDF profile, generic-vision profile, text-only fallback), so a provider swap (e.g. Groq via the OpenAI-compatible integration) is configuration, not code. Enforced by a CI run of the whole workflow against a stub `IChatClient`.

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

- MAF and its `Microsoft.Agents.AI.Anthropic` integration are young (the latter is prerelease): pin exact package versions and model strings; keep provider types behind the adapter; expect breaking changes on upgrade.
- Provider capability drift: matrix extraction quality is provider-dependent — the eval harness reports metrics per provider profile, and a profile swap requires a green regression run.
- Don't trust model-reported confidence at face value — thresholds are calibrated against observed correction rates.
- No cell bounding boxes from PDF mode: provenance is page + verbatim snippet, and the review UI locates sources by snippet text-match.
- Reqnroll's default telemetry egresses to Azure App Insights (violates the no-Azure constraint). It is disabled via `REQNROLL_TELEMETRY_ENABLED=0`: the checked-in `.runsettings` covers `dotnet test`, CI sets it job-wide, and developers should set it machine-wide to cover local `dotnet build` (the MSBuild code-gen task also transmits).
- The test stack is pinned to xUnit **v2** because `Reqnroll.xUnit` requires `xunit.core 2.x`. Moving to xunit v3 means switching to `Reqnroll.xunit.v3` — never bump the xunit pin in isolation.
- Raising a vulnerable *transitive* package via a synthetic direct `PackageReference` can trip `error NU1510` (package-pruning) under warnings-as-errors on net10.0. If that situation arises, `CentralPackageTransitivePinningEnabled` in `Directory.Packages.props` is the clean lever — it was deliberately left off (redundant with locked-mode CI restore for reproducibility) but is the right tool for transitive version raises.
