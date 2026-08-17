# CBIX — Cross-Border Instruction Extraction Service

Extract per-country Cross-Border Trading Legal Instruction PDFs into validated, provenance-tracked relational data.

## What it is

CBIX converts regulatory instruction documents (PDFs) into queryable relational data. The extraction pipeline is a Microsoft Agent Framework Workflows orchestrator (.NET) where LLM agents propose structured candidates (document sections, permission matrices, footnote resolutions, taxonomy normalisations) and deterministic code validates and persists them.

Planned technology stack:
- **.NET worker** running Microsoft Agent Framework Workflows
- **Anthropic Claude API** for extraction (native PDF mode + structured outputs)
- **PDFPig** for document text layer
- **SQL Server** for checkpoints, staging, and published schema
- **No Azure services**

Governing principle: **agents propose, code disposes.** LLM agents never touch the database. They produce typed candidates; plain code validates (grounding checks, referential integrity, completeness gates), persists, and audits.

## Status

Sprint 01 of milestone M0 (PoC) is **complete** — all 17 stories shipped: the .NET solution, CI with a locked-restore and dependency-audit gate, the `IDocumentContentProvider` port with three profiles (Claude native-PDF, generic-vision, text-only), hardened ingest (content-hash dedupe, path containment, PDFPig text layer), the Anthropic provider adapter, secrets-manager key sourcing, the minimal MAF workflow (ingest → triage → DocControl → persist stub) with review routing, the CI-enforced LLM-agnosticism proof, and DocControl extraction with grounded `ExtractedField<T>` provenance. Sprint 02 (remaining six section agents, validator gates, persistence, golden-set eval) has not started. The [solution design document](docs/Cross_Border_Instruction_Extraction_Solution_Design.md) (Draft v0.3, with a Sprint-01 §11 addendum) remains the authoritative architecture, data model, and delivery phasing.

## Repository layout

| File | Purpose |
|------|---------|
| `docs/Cross_Border_Instruction_Extraction_Solution_Design.md` | Complete architecture, data model, and engineering decisions |
| `plan/` | Implementation plan: milestone roadmap (M0–M3) and 2-week sprint files with BDD user stories |
| `src/` | `Cbix.Core` (contracts, ingest, workflow), `Cbix.Providers.Anthropic` (the sole provider adapter), `Cbix.Worker` (thin host + composition) |
| `tests/` | `Cbix.UnitTests` (xUnit), `Cbix.Bdd` (Reqnroll BDD), `Cbix.Agnosticism` (provider-free stub assembly backing the CI agnosticism gate) |
| `db/schema/` | T-SQL DDL for the operational tables (`document_registry`, `review_queue`) per design §6 |
| `THIRD-PARTY-NOTICES.md` | Attribution for redistributed binaries (PDFtoImage/SkiaSharp/PDFium and PDFium's bundled components) |
| `data/Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf` | Synthetic specimen: fictional German cross-border instruction |
| `data/Cross_Border_Trading_Legal_Instruction_CH_SPECIMEN.pdf` | Synthetic specimen: fictional Swiss cross-border instruction |
| `data/cbti_country_ground_truth.json` | Golden set v0: expected extraction for both specimens |
| `CLAUDE.md` | Repository architecture summary and development guidance |
| `LICENSE` | MIT license |

## Synthetic data

All data in this repository is synthetic. The specimen PDFs are fictional documents for "Contoso Bank" created to exercise the extraction pipeline. They contain no real client, entity, or account data. They are suitable for public sharing and development/testing before production use with real regulatory documents.

## License

MIT — see [LICENSE](LICENSE) for terms.
