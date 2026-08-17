# Sprint 02 — Extraction, validation and evaluation

**Milestone:** M0 — PoC

**Sprint goal:** Complete all seven section agents including the Matrix agent, wire fan-out/fan-in and the normaliser, implement every deterministic validator gate and the targeted retry loop, persist to the published schema, and prove the M0 exit criteria on the golden set with a measured cost-per-document figure.

**Sprint exit criteria:**
- All seven section agents run concurrently via fan-out/fan-in against both specimens.
- All five validator gate families (schema/enum, grounding, referential integrity, completeness, supersession) are implemented in design order and wired into the retry/persist/review edges.
- The persist executor publishes DE and CH runs transactionally with effective dating and full provenance.
- The golden-set eval harness computes all four §5.9 metrics and is a required CI gate.
- M0 exit criteria are measured and recorded: ≥98% matrix cell accuracy, ≥95% scalar exact-match, grounding and referential-integrity at 100%, and a written cost-per-document figure.

---

### [ ] S02-01 — Fan-out/fan-in extended to all seven section agents

As the pipeline
I want the workflow topology extended so triage fans out to all seven section agents in parallel and a fan-in barrier aggregates their results
So that section extraction runs concurrently rather than one agent at a time (design §5.2, §5.4)

Depends on: S01-13, S01-16
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Fan-out to seven section agents
  Scenario: All seven agents run against the DE specimen and are aggregated
    Given the DE specimen's DocumentProfile from triage
    When the workflow executes the fan-out stage
    Then DocControl, Entities, Matrix, Conditions, Marketing, Tax and VersionHistory agents all run
    And the fan-in barrier waits for all seven before producing an aggregate result
    And a single failing section agent does not block the other six from completing
```

Done means:
- Aggregate result shape carries all seven section outputs keyed by section name, ready for the normaliser (S02-09).
- **Agnosticism-gate root set (inherited, Sprint 01 final review):** the LLM-agnosticism gate's reachability walk hand-keeps a resolved-concretes root list (`tests/Cbix.Bdd/Support/WorkflowRunDependencyGraph.cs`). The six new keyed section agents must each join it — or, better, the set must be derived by enumerating keyed `AIAgent`/factory registrations off the service collection, which that comment names as the correct fix. A missing root only shrinks the walk, which is exactly how the permanent gate rots silently.
- **Cache-priming stagger (inherited from S01-13's Done means):** the section fan-out must be a successor of `triage`, never a peer of it — a peer fan-out all cache-misses and pays the write premium; the run would be correct and the bill wrong. The graph-shape test pinning every model-calling node as a descendant of triage must stay green with the widened set.
- **Worker intake loop (Sprint 01 final review):** nothing outside the test harness runs the workflow — `Cbix.Worker`'s `Worker.cs` is still the S01-01 placeholder heartbeat, and design §5.2's SQL-table work queue has no owner. This story (or a sibling added at Sprint 02 planning) must give the worker a real intake: drain the SQL-table queue (or an interim file-drop poll explicitly recorded as such) and drive `CbixWorkflowFactory` runs. The roadmap coverage row for §5.2's work queue has been corrected from "Sprint 01 (implicit)" to this deferral.

---

### [ ] S02-02 — Entities agent

As the pipeline
I want a Haiku-tier Entities agent producing an `EntitiesSection` from the document
So that entities and legal basis are extracted with the same provenance discipline as DocControl (design §5.4)

Depends on: S02-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Entities agent
  Scenario: Entities extracted from the DE specimen
    Given the DE specimen's document handle
    When the Entities agent runs
    Then it returns an EntitiesSection with at least one entity record
    And every scalar field is wrapped in ExtractedField<T> with a verbatim SourceSnippet
```

Done means: —

---

### [ ] S02-03 — Matrix agent extracts the permission matrix with exact cell count

As the pipeline
I want a Sonnet-tier Matrix agent that reads the permission matrix visually and emits exactly `products × categories` cells
So that the headline accuracy metric (matrix cell accuracy) has a correctly shaped candidate to score (design §5.4)

Depends on: S02-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Matrix agent cell count
  Scenario: DE specimen produces exactly 40 matrix cells
    Given the DE specimen's document handle (10 products x 4 MiFID II categories)
    When the Matrix agent runs
    Then it returns a MatrixSection with exactly 40 PermissionCell records
    And every cell has a StatusCode in {"P","PR","RS","NP","N/A"}
  Scenario: CH specimen produces exactly 30 matrix cells
    Given the CH specimen's document handle (10 products x 3 FinSA categories)
    When the Matrix agent runs
    Then it returns a MatrixSection with exactly 30 PermissionCell records
```

Done means: `PermissionCell` shape matches design Appendix A (`Product, ClientCategoryRaw, StatusCode, ConditionRefs, SourcePage`) exactly.

---

### [ ] S02-04 — Matrix agent escalation: focused re-run on matrix pages

As the pipeline
I want a failed Matrix extraction to trigger a focused re-run against only the matrix pages with the validator's findings in context, before falling back to model-tier escalation
So that matrix failures are retried efficiently rather than re-running whole-document extraction (design §5.4, §9 "Matrix misread" row)

Depends on: S02-03
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Matrix agent escalation
  Scenario: Cell-count failure triggers a focused matrix-pages retry
    Given a Matrix extraction that returned 38 cells instead of 40 for the DE specimen
    When the retry edge invokes the Matrix agent again
    Then the re-run is scoped to only the pages containing the matrix
    And the validator's missing-cell findings are included in the retry prompt context
```

Done means: this story wires the escalation path; it depends on the completeness gate existing (S02-14) to produce the findings it consumes — sequence accordingly during implementation even though the story is listed here per the design's agent-focused grouping.

---

### [ ] S02-05 — Conditions agent

As the pipeline
I want a Haiku-tier Conditions agent producing a `ConditionsSection` of footnote/condition items with their reference numbers
So that matrix cells' condition references (S02-13) have something to resolve against (design §5.4)

Depends on: S02-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Conditions agent
  Scenario: Conditions extracted from the DE specimen
    Given the DE specimen's document handle
    When the Conditions agent runs
    Then it returns a ConditionsSection with one ConditionItem per footnote reference printed in the document
    And each ConditionItem's Ref matches the superscript number used in the matrix
```

Done means: `ConditionsSection`/`ConditionItem` shape matches design Appendix A exactly.

---

### [ ] S02-06 — Marketing agent

As the pipeline
I want a Haiku-tier Marketing agent producing a `MarketingSection` for marketing and solicitation rules
So that this section is extracted with the same contract discipline as the others (design §5.4)

Depends on: S02-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Marketing agent
  Scenario: Marketing rules extracted from the DE specimen
    Given the DE specimen's document handle
    When the Marketing agent runs
    Then it returns a MarketingSection with at least one rule record
    And every scalar field is wrapped in ExtractedField<T>
```

Done means: —

---

### [ ] S02-07 — Tax agent

As the pipeline
I want a Haiku-tier Tax agent producing a `TaxSection` for tax considerations
So that tax notes are extracted with the same contract discipline as the others (design §5.4)

Depends on: S02-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Tax agent
  Scenario: Tax notes extracted from the DE specimen
    Given the DE specimen's document handle
    When the Tax agent runs
    Then it returns a TaxSection with at least one tax note record
    And every scalar field is wrapped in ExtractedField<T>
```

Done means: —

---

### [ ] S02-08 — VersionHistory agent

As the pipeline
I want a Haiku-tier VersionHistory agent producing a `VersionHistorySection` from the version-history table
So that version records feed the completeness gate's monotonic-ordering check (S02-15) (design §5.4)

Depends on: S02-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: VersionHistory agent
  Scenario: Version history extracted from the DE specimen
    Given the DE specimen's document handle
    When the VersionHistory agent runs
    Then it returns a VersionHistorySection with one record per row of the printed version-history table
    And each record carries a version identifier and an effective date
```

Done means: —

---

### [ ] S02-09 — Normaliser: dictionary-first category lookup

As the pipeline
I want the normaliser to resolve raw client-category values (MiFID II for DE, FinSA for CH) against a maintained dictionary before invoking any agent
So that known values are mapped deterministically at zero LLM cost (design §5.5)

Depends on: S02-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Normaliser dictionary-first lookup
  Scenario: A known DE category maps deterministically
    Given the category_mappings dictionary contains an entry for "Professional Client" -> canonical "PROFESSIONAL"
    When the normaliser processes a matrix cell with ClientCategoryRaw "Professional Client"
    Then ClientCategoryCanonical is set to "PROFESSIONAL"
    And no agent call is made for this value
  Scenario: A known CH category maps deterministically
    Given the category_mappings dictionary contains an entry for "Institutional Client" -> canonical "INSTITUTIONAL"
    When the normaliser processes a matrix cell with ClientCategoryRaw "Institutional Client"
    Then ClientCategoryCanonical is set to "INSTITUTIONAL"
```

Done means: dictionary storage matches `category_mappings` per design §6.

---

### [ ] S02-10 — Normaliser: unmapped values via agent tool call, emitted as UNMAPPED

As the pipeline
I want the normaliser agent invoked only for values the dictionary cannot resolve, using `AIFunction` reference-data tools, and emitting `UNMAPPED` when it cannot map with high confidence
So that unknown taxonomy values are never guessed into a wrong canonical category (design §5.5, §9 "Unknown client-category label" row)

Depends on: S02-09
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Normaliser unmapped handling
  Scenario: An unrecognised category is emitted as UNMAPPED and routed to review
    Given a matrix cell with ClientCategoryRaw "Semi-Professional Investor" not present in category_mappings
    When the normaliser processes this cell
    Then it invokes the agent with the canonical enum and definitions in context
    And if the agent cannot map with high confidence, ClientCategoryCanonical is set to "UNMAPPED"
    And the run is routed toward review for this field
```

Done means: entity-master/LEI reference-data lookups are exposed as `AIFunction` tools per design §5.5, even if this story's scenario only exercises the category-mapping tool.

---

### [ ] S02-11 — Validator gate: schema and enum conformance

As the pipeline
I want the validator's first gate to check every section's output against its JSON schema and enum constraints
So that malformed or out-of-range values never reach later gates or persistence (design §5.6, gate order item 1)

Depends on: S02-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Validator schema and enum gate
  Scenario: A status code outside the allowed set fails the gate
    Given a MatrixSection containing a cell with StatusCode "XX"
    When the validator runs the schema/enum gate
    Then the gate fails
    And the ValidationReport names the offending cell and the allowed values {"P","PR","RS","NP","N/A"}
  Scenario: A conformant section passes the gate
    Given a DocControlSection with all fields matching their declared types and enums
    When the validator runs the schema/enum gate
    Then the gate passes
```

Done means: this gate runs first, per design §5.6's explicit gate ordering.

---

### [ ] S02-12 — Validator gate: grounding (snippet containment)

As the pipeline
I want the validator's second gate to check that every `SourceSnippet` appears verbatim in the PDFPig text layer for its reported page
So that hallucinated or paraphrased snippets are caught by the cheapest available defence (design §5.6, gate order item 2)

Depends on: S02-11, S01-11
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Validator grounding gate
  Scenario: A verbatim snippet passes
    Given a DocControlSection field whose SourceSnippet is copied exactly from page 1 of the DE specimen's text layer
    When the validator runs the grounding gate
    Then the gate passes for that field
  Scenario: A paraphrased snippet fails
    Given a field whose SourceSnippet does not appear verbatim anywhere on its reported SourcePage
    When the validator runs the grounding gate
    Then the gate fails
    And the ValidationReport includes "snippet not found on page <N>" for that field
```

Done means:
- M0 exit criterion requires this gate at 100% "by construction" — the gate itself must have zero false negatives against the golden set's known-correct snippets (verified by S02-24).
- **Scope decision (recorded at Sprint 01 close):** the gate checks snippet-appears-verbatim-on-its-page ONLY — it does NOT check snippet-contains-value. A document printing "16 August 2026" with the model returning `Value: "2026-08-16"` is a legitimate normalised extraction whose snippet grounds correctly; enforcing containment of the value in the snippet would false-refuse it. If value-in-snippet checking is ever wanted, it needs its own story with a normalisation-aware comparison — do not fold it into this gate. (The extraction prompts instruct the model that the snippet must contain the value; that is prompt guidance, not a validator contract.)
- **Rendering rule (inherited):** stored snippets stay raw (ordinal containment needs verbatim bytes); every RENDERING of a snippet into `ValidationReport` text, review-queue rows, logs, or an operator's terminal goes through `ExtractionText.ForMessage` — the split is documented at the S01-16 grounding assertion, which this validator supersedes.

---

### [ ] S02-13 — Validator gate: referential integrity

As the pipeline
I want the validator's third gate to check that every condition reference cited in a matrix cell resolves to an extracted condition
So that hallucinated footnote references are caught before persistence (design §5.6, gate order item 3; §9 "Hallucinated condition reference" row)

Depends on: S02-12, S02-05
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Validator referential integrity gate
  Scenario: All condition references resolve
    Given a MatrixSection whose cells cite ConditionRefs {1,2,3}
    And a ConditionsSection containing ConditionItems with Refs {1,2,3,4}
    When the validator runs the referential-integrity gate
    Then the gate passes
  Scenario: An unresolved condition reference fails
    Given a MatrixSection cell citing ConditionRef 7
    And a ConditionsSection with no ConditionItem whose Ref is 7
    When the validator runs the referential-integrity gate
    Then the gate fails
    And the ValidationReport names condition reference 7 and the offending cell
```

Done means: M0 exit criterion requires this gate at 100% "by construction" — verified by S02-24.

---

### [ ] S02-14 — Validator gate: completeness — matrix cell count

As the pipeline
I want the validator's completeness gate to check the matrix contains exactly `products × categories` cells
So that silently missing cells are caught deterministically (design §5.6, gate order item 4; §9 "Matrix misread" row)

Depends on: S02-13, S02-03
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Validator completeness gate - cell count
  Scenario: DE specimen matrix has all 40 cells
    Given a MatrixSection for the DE specimen with 40 cells covering all 10 products x 4 categories
    When the validator runs the completeness gate's cell-count check
    Then the gate passes
  Scenario: DE specimen matrix is missing a cell
    Given a MatrixSection for the DE specimen with 39 cells, missing (OTC derivatives - uncleared, Professional)
    When the validator runs the completeness gate's cell-count check
    Then the gate fails
    And the ValidationReport states "cell (OTC derivatives - uncleared, Professional) missing"
```

Done means: —

---

### [ ] S02-15 — Validator gate: completeness — version history monotonic ordering

As the pipeline
I want the completeness gate to check that extracted version-history entries are monotonically ordered
So that an out-of-order version history is caught before it corrupts the supersession chain (design §5.6, gate order item 4)

Depends on: S02-14, S02-08
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Validator completeness gate - version history ordering
  Scenario: Monotonic version history passes
    Given a VersionHistorySection with versions 1.0, 2.0, 3.1 in increasing order by effective date
    When the validator runs the completeness gate's version-history check
    Then the gate passes
  Scenario: Out-of-order version history fails
    Given a VersionHistorySection with versions 1.0, 3.1, 2.0 where 2.0's effective date precedes 3.1's
    When the validator runs the completeness gate's version-history check
    Then the gate fails
    And the ValidationReport identifies the out-of-order entry
```

Done means: —

---

### [ ] S02-16 — Validator gate: completeness — effective date precedes review date

As the pipeline
I want the completeness gate to check that a document's effective date precedes its review date
So that an internally inconsistent document-control block is caught (design §5.6, gate order item 4)

Depends on: S02-15, S01-16
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Validator completeness gate - date ordering
  Scenario: Effective date before review date passes
    Given a DocControlSection with EffectiveDate 2026-01-01 and ReviewDate 2027-01-01
    When the validator runs the completeness gate's date-ordering check
    Then the gate passes
  Scenario: Effective date after review date fails
    Given a DocControlSection with EffectiveDate 2027-01-01 and ReviewDate 2026-01-01
    When the validator runs the completeness gate's date-ordering check
    Then the gate fails
    And the ValidationReport flags the effective/review date inversion
```

Done means: —

---

### [ ] S02-17 — Validator gate: supersession warning (non-blocking)

As the pipeline
I want a cross-document check that warns, but does not fail, when a document's `supersedes` reference predates the registry
So that unresolvable-but-plausible supersession references don't block otherwise-valid runs (design §5.6, gate order item 5)

Depends on: S02-16
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Validator supersession warning
  Scenario: supersedes reference resolves to a known prior version
    Given a document declaring it supersedes doc_ref/version "CBTI-DE/3.1" which exists in the registry
    When the validator runs the cross-document check
    Then no warning is raised
  Scenario: supersedes reference predates the registry
    Given a document declaring it supersedes a version with no matching registry record
    When the validator runs the cross-document check
    Then a warning is recorded on the ValidationReport
    And the gate does not fail the run
```

Done means: warnings are visible in the persisted `extraction_runs` outcome even though they do not block persist.

---

### [ ] S02-18 — Targeted retry loop with structured feedback, max 2 attempts

As the pipeline
I want a failing gate to re-invoke only the offending section agent, with the ValidationReport appended to its context, for at most two attempts before escalating
So that the retry loop pays for itself instead of re-running the whole document (design §5.6)

Depends on: S02-11, S02-12, S02-13, S02-14, S02-15, S02-16
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Targeted retry loop
  Scenario: First failure retries only the failing agent
    Given a Matrix gate failure on attempt 1 for the DE specimen
    When the retry edge fires
    Then only the Matrix agent re-runs, not the other six section agents
    And the retry prompt includes "Fix only the following issues: ..." with the ValidationReport's findings
  Scenario: Exhausted retries escalate to review
    Given the Matrix gate has failed on attempts 1 and 2
    When the validator evaluates attempt 2's result and it still fails
    Then the run routes to the review queue instead of retrying a third time
```

Done means: attempt counter is per-section, matching the `r.Attempts < 2` condition in design §5.2's illustrative topology.

---

### [ ] S02-19 — Persist executor: staging write

As the pipeline
I want the persist executor to write the full run output to a staging schema before touching published tables
So that publish is a single atomic step over already-validated staged data (design §5.8)

Depends on: S02-18
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Persist staging write
  Scenario: A passed run is written to staging
    Given a DE specimen run that passed all validator gates
    When the persist executor runs
    Then all section data is written to staging tables
    And no published table is modified yet
```

Done means: —

---

### [ ] S02-20 — Persist executor: transactional publish with effective dating

As the pipeline
I want the persist executor to publish staged data to the target schema in a single transaction, effective-dated on `(doc_ref, doc_version)`, closing the prior version's validity window rather than deleting it
So that the supersession chain the documents declare is preserved in the published schema (design §5.8)

Depends on: S02-19
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Persist transactional publish
  Scenario: First publish of a document opens a validity window
    Given staged data for CBTI-DE version 3.1 with no prior published version
    When the persist executor publishes
    Then dbo.country_documents gets a row for (CBTI-DE, 3.1) with valid_to NULL
  Scenario: Publishing a new version closes the prior window
    Given CBTI-DE version 3.1 is currently published with valid_to NULL
    And staged data for CBTI-DE version 3.2 has passed validation
    When the persist executor publishes version 3.2
    Then version 3.1's valid_to is set to a non-null timestamp
    And version 3.2 is published with valid_to NULL
    And both operations commit in a single transaction
```

Done means: schema matches design §6's `country_documents` table exactly (PK `doc_ref, doc_version`, `valid_from`/`valid_to`).

---

### [ ] S02-21 — extraction_runs and field_provenance audit persistence

As an auditor
I want every publish to write an `extraction_runs` record and one `field_provenance` row per extracted field
So that any published value can answer, in one query, its document/page, verbatim source, model, prompt version, confidence and reviewer (design §5.8, §8 Auditability)

Depends on: S02-20
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Audit trail persistence
  Scenario: A published run has a matching extraction_runs record
    Given a DE specimen run that was just published
    When I query dbo.extraction_runs for this run
    Then it records model configuration, prompt versions, token cost and outcome
  Scenario: Every published field has a provenance row
    Given the published DocControl section for CBTI-DE version 3.1
    When I query dbo.field_provenance for this document/version
    Then every published scalar field has a row with source_page, source_snippet, confidence, model_id and prompt_version populated
    And reviewed_by/reviewed_at are NULL for fields that were never reviewed
```

Done means: `field_provenance` and `extraction_runs` are append-only, per design §8.

---

### [ ] S02-22 — Golden-set eval harness computes all four metrics

As a developer
I want a CI harness that replays the golden set and computes field-level exact-match precision/recall, matrix cell accuracy, condition-linkage F1 and category-normalisation accuracy
So that extraction quality is measurable, not just eyeballed (design §5.9)

Depends on: S02-20, S01-16
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Golden-set eval harness metrics
  Scenario: Harness computes all four metrics against the golden set
    Given "data/cbti_country_ground_truth.json" and the DE/CH specimens
    When the eval harness runs the full pipeline and compares output to ground truth
    Then it reports field-level exact-match precision and recall
    And it reports matrix cell accuracy as a single percentage
    And it reports condition-linkage F1
    And it reports category-normalisation accuracy
```

Done means: metric definitions match design §5.9 exactly; matrix cell accuracy is explicitly labelled the headline metric in the harness's report output.

---

### [ ] S02-23 — Golden-set eval harness wired into CI as a required gate

As a developer
I want the eval harness to run in CI and block merges when it fails
So that no prompt, model version or configuration change ships without a green golden-set run (design §5.9)

Depends on: S02-22
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Golden-set CI gate
  Scenario: A regression in matrix cell accuracy blocks the pipeline
    Given a change that drops matrix cell accuracy below the configured threshold
    When CI runs the eval harness
    Then the CI job fails
    And the failure output names which metric regressed and by how much
```

Done means: threshold values used here are placeholders until S02-24 confirms the real M0 exit thresholds; this story only needs the gate mechanism to exist and to fail correctly.

---

### [ ] S02-24 — M0 exit-criteria measurement: accuracy thresholds and cost-per-document

As the milestone owner
I want a single measured run against the golden set that reports matrix cell accuracy, scalar exact-match, gate pass rates and cost per document
So that M0's exit criteria are confirmed with real numbers, not assumed (design §10 Phase 0)

Depends on: S02-23, S02-21
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: M0 exit criteria measurement
  Scenario: M0 exit criteria are met on the golden set
    Given the full pipeline running against the DE and CH specimens with the golden set as ground truth
    When the M0 measurement run completes
    Then matrix cell accuracy is at least 98%
    And scalar exact-match is at least 95%
    And the grounding gate pass rate is 100%
    And the referential-integrity gate pass rate is 100%
    And a cost-per-document figure in USD is recorded from real API usage, using the token-counting endpoint or actual billed tokens
```

Done means: the measured figures (accuracy percentages and cost-per-document) are written into this plan's roadmap file or a linked results note as the M0 exit-criteria record, per design §10's "written cost-per-document figure from real runs."
