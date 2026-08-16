# Sprint 01 — Scaffolding and provider abstraction

**Milestone:** M0 — PoC

**Sprint goal:** Stand up the .NET solution, BDD tooling and CI, prove LLM-agnosticism as an executable fact, and run one section extractor (DocControl) end-to-end against the DE specimen through a minimal workflow.

**Sprint exit criteria:**
- Solution builds, and the full test suite (unit + Reqnroll/BDD) runs green in CI on every push.
- The workflow skeleton runs end-to-end against a stub `IChatClient` in CI with zero Anthropic package reference on that code path (design §3, §7 — the enforceable agnosticism criterion).
- `DocControlSection` is extracted end-to-end from the DE specimen (`data/Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf`) via the real Claude profile, with provenance populated on every scalar field.
- Triage correctly profiles both specimens (DE and CH).

---

### [x] S01-01 — Solution scaffolding compiles and tests run

As a developer
I want a .NET solution with a worker project, a unit-test project and a BDD-test project wired together
So that subsequent stories have somewhere to put code and tests from day one

Depends on: —
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Solution scaffolding
  Scenario: Solution builds and both test projects run
    Given the CBIX solution at the repository root
    When I run "dotnet build" on the solution
    Then the build succeeds with zero errors
    And running "dotnet test" executes both the unit-test project and the BDD-test project
    And at least one placeholder test passes in each project
```

Done means:
- Solution layout: `src/Cbix.Worker`, `src/Cbix.Core` (contracts/executors), `tests/Cbix.UnitTests`, `tests/Cbix.Bdd` (Reqnroll).
- `CLAUDE.md` updated with the real build/test commands (it currently states none exist).
- Bootstrap exemption (recorded): roadmap DoD item 1 — failing Reqnroll scenario first — is waived for this story only, because the Reqnroll harness it presupposes is itself delivered by S01-02. Acceptance was proven by direct execution instead (build/test/format exit codes). No other story inherits this exemption.

---

### [x] S01-02 — Reqnroll + xUnit wired and runs a real Gherkin scenario

As a developer
I want Reqnroll bound to xUnit in `Cbix.Bdd`
So that every story going forward can start from a failing `.feature` file per the working agreement

Depends on: S01-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: BDD harness
  Scenario: A Gherkin feature drives a step definition and reports a real failure
    Given a ".feature" file with a scenario "Sample scenario" containing an unimplemented step
    When "dotnet test" runs the BDD project
    Then the scenario is reported as failing due to the missing step binding, not a compile error
    And after the step binding is added the same scenario passes
```

Done means:
- Reqnroll test runner and xUnit adapter referenced in `Cbix.Bdd`; `reqnroll.json` configured for xUnit.
- This story's own scenario is the template subsequent stories copy for their "failing executable scenario first" step.

---

### [ ] S01-03 — CI skeleton runs build and full test suite on every push

As a developer
I want a CI pipeline that builds the solution and runs all tests on every push
So that the golden-set gate and the agnosticism proof (later stories) have somewhere to run automatically

Depends on: S01-01, S01-02
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: CI skeleton
  Scenario: Pipeline runs on push and reports pass/fail
    Given a commit pushed to any branch
    When the CI pipeline executes
    Then it restores, builds and runs "dotnet test" for the whole solution
    And the pipeline fails if any test fails or the build has warnings-as-errors violations
```

Done means:
- No Azure-hosted CI service used for anything that would violate the no-Azure constraint on the runtime platform; CI tooling itself (e.g. GitHub Actions) is acceptable since it is build infrastructure, not part of the deployed system — noted explicitly in the pipeline file's header comment to avoid future confusion.
- Restore runs locked: the pipeline sets `ContinuousIntegrationBuild=true` (activating the CI-conditional `RestoreLockedMode` in `Directory.Build.props`) or passes `--locked-mode` explicitly — committed `packages.lock.json` files are only a control when restore is locked.
- A dedicated dependency-audit step hard-fails on known-vulnerable packages (`dotnet list package --vulnerable --include-transitive`); NU190x audit warnings are excluded from warnings-as-errors locally precisely so advisories surface here, actionably, instead of breaking developer builds at random times.
- `REQNROLL_TELEMETRY_ENABLED=0` exported at job level — covers the build-time telemetry path that the checked-in `.runsettings` (test-run scope) cannot reach.
- `dotnet format Cbix.sln --verify-no-changes` runs as a pipeline step.

---

### [ ] S01-04 — `IDocumentContentProvider` port defined

As a developer
I want a document-content-provider abstraction with no provider-specific types in its signature
So that Claude, generic-vision and text-only profiles can implement it interchangeably (design §5.1)

Depends on: S01-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Document content provider port
  Scenario: The port exposes only framework-neutral types
  Given the "IDocumentContentProvider" interface in "Cbix.Core"
  When I inspect its method signatures
  Then no method references any Anthropic-specific type
  And the return type carries a capability flag indicating whether visual (image) content is included
```

Done means:
- Interface lives in `Cbix.Core`, referenced by both the ingest executor and section agents; no implementation yet (implementations are S01-05/06/07).

---

### [ ] S01-05 — Claude document-content profile (Files API + cache_control)

As the pipeline
I want the Claude profile of `IDocumentContentProvider` to upload a document once to the Claude Files API and reference it by `file_id` with `cache_control` set
So that every section agent reads the cached document without re-uploading (design §5.1)

Depends on: S01-04
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Claude document content profile
  Scenario: DE specimen uploaded once and referenced by file_id
    Given the DE specimen "data/Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf"
    When the Claude profile prepares document content for a first agent call
    Then the PDF is uploaded to the Claude Files API exactly once
    And the returned content block references the resulting "file_id"
    And "cache_control" is set on the document block
    Scenario: A second agent call against the same document reuses the file_id
    Given the DE specimen was already uploaded in this run
    When the Claude profile prepares document content for a second agent call
    Then no second upload call is made
    And the same "file_id" is reused
```

Done means:
- All Claude-specific types (raw-representation `MessageCreateParams`, `AnthropicClient.Beta.Files`) live only inside this profile implementation, per design §3's "confined to one provider adapter" rule.

---

### [ ] S01-06 — Generic-vision document-content profile

As the pipeline
I want a provider-agnostic fallback profile that sends the PDFPig text layer plus locally rendered page images as ordinary multimodal content
So that a non-Claude provider without native PDF mode can still read the visual matrix (design §5.1)

Depends on: S01-04
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Generic vision document content profile
  Scenario: Document content is built from local text and rendered images
    Given the DE specimen and its PDFPig-extracted text layer
    When the generic-vision profile prepares document content
    Then the content includes the extracted text
    And the content includes one rendered image per page
    And no Files API or Anthropic-specific type is referenced anywhere in this profile
```

Done means: profile is selected purely by the active provider's capability profile, per design §5.1's "only the strategy implementations know which profile is in play."

---

### [ ] S01-07 — Text-only document-content profile flags degraded mode

As the pipeline
I want a text-only fallback profile that sends only the PDFPig text layer, with an explicit degraded-mode flag on its output
So that a provider or configuration lacking vision support is never silently treated as equivalent to full visual extraction (design §5.1)

Depends on: S01-04
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Text-only document content profile
  Scenario: Text-only content is flagged as degraded
    Given the DE specimen's PDFPig text layer
    When the text-only profile prepares document content
    Then the content contains no image blocks
    And the returned capability flag reports "visual content: false"
```

Done means: the eval harness (Sprint 02) is expected to consume this flag when it exists; this story only guarantees the flag is produced, not yet consumed.

---

### [ ] S01-08 — Anthropic provider adapter via `Microsoft.Agents.AI.Anthropic`

As a developer
I want the Claude model client wired through `Microsoft.Agents.AI.Anthropic`'s `AnthropicClient.AsAIAgent(...)`
So that agents in the workflow are ordinary MAF `AIAgent` instances indistinguishable from any other provider (design §3, §7)

Depends on: S01-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Anthropic provider adapter
  Scenario: An agent built from the adapter is a standard AIAgent
    Given an Anthropic API key configured via the secrets-manager pattern
    When the provider adapter constructs an agent named "docControl" with a model and instructions
    Then the returned object's static type is MAF's "AIAgent"
    And no code outside "Cbix.Providers.Anthropic" references any type from the "Microsoft.Agents.AI.Anthropic" package
```

Done means:
- Package pinned to an exact prerelease version (design §11 caution — young, breaking-change risk).
- Adapter is the sole location for raw-representation escape-hatch calls (`RawRepresentationFactory`), per design §3.

---

### [ ] S01-09 — LLM-agnosticism proof: workflow runs against a stub `IChatClient`

As the architecture owner
I want the full workflow skeleton to run against a stub `IChatClient` with zero Anthropic dependency on that path
So that LLM-agnosticism is a CI-enforced fact, not an aspiration (design §3 —"that run is the acceptance criterion for agnosticism")

Depends on: S01-04, S01-08, S01-13
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: LLM agnosticism proof
  Scenario: Workflow completes end-to-end against a stub chat client
    Given a stub "IChatClient" implementation that returns canned structured-output JSON
    And the workflow is configured with the stub client instead of the Anthropic adapter
    When the workflow runs against a test document
    Then the run completes to the persist step
    And no assembly reachable from this run's dependency graph is "Microsoft.Agents.AI.Anthropic"
```

Done means:
- This scenario runs in CI on every push (not just Sprint 01) — it is the permanent regression gate for agnosticism, referenced from `plan/00-roadmap.md`'s coverage table.
- A build-time or test-time dependency check (e.g. asserting the test project's transitive references) backs the "zero Anthropic dependency" claim; a passing run alone is not sufficient evidence.

---

### [ ] S01-10 — Ingest executor: content hash and dedupe registry

As the pipeline
I want the ingest executor to compute a content hash for every incoming document and record it in a registry table
So that re-submissions are idempotent and duplicates are detected before any LLM call (design §5.1, §8 Idempotency)

Depends on: S01-01
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Ingest content hash and dedupe
  Scenario: First submission is registered
    Given the DE specimen has not been submitted before
    When the ingest executor processes it
    Then a content hash is computed
    And a new registry record is created with that hash
  Scenario: Duplicate submission is a no-op
    Given the DE specimen was already registered with its content hash
    When the same file is submitted again unchanged
    Then the ingest executor makes no new registry record
    And an audit log entry records the duplicate (design §9 "Duplicate submission" row)
```

Done means: registry table matches `document_registry` per design §6's operational-tables list.

---

### [ ] S01-11 — Ingest executor: PDFPig text layer extraction

As the pipeline
I want the ingest executor to extract a local text layer with PDFPig for every registered document
So that the validator's grounding gate (Sprint 02) has a free, local corpus to check snippets against (design §5.1, §5.6)

Depends on: S01-10
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: PDFPig text layer extraction
  Scenario: Text layer extracted per page for the DE specimen
    Given the registered DE specimen
    When the ingest executor extracts the text layer
    Then a text string is produced for every page in the document
    And the per-page text is retrievable by logical page number
```

Done means: extracted text is persisted (or held in run state) such that Sprint 02's grounding gate can query it without re-parsing the PDF.

---

### [ ] S01-12 — Ingest executor uploads via the Claude profile and sets cache_control

As the pipeline
I want ingest to invoke the Claude document-content profile exactly once per document
So that the Files API upload and cache_control setup (S01-05) happen at ingest time, before triage or any section agent runs

Depends on: S01-05, S01-11
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Ingest triggers Claude upload
  Scenario: Ingest produces a reusable document handle
    Given the registered DE specimen with its PDFPig text layer
    When the ingest executor runs with the Claude profile active
    Then the Claude Files API upload happens exactly once
    And the resulting file_id is attached to the run's document handle for downstream agents
```

Done means: the document handle produced here is what triage (S01-14) and the DocControl agent (S01-16) consume.

---

### [ ] S01-13 — Minimal `WorkflowBuilder` topology: ingest → triage → one agent

As a developer
I want a minimal MAF `WorkflowBuilder` graph connecting ingest, triage and a single downstream agent
So that later sprints extend a working topology instead of building one from scratch (design §5.2)

Depends on: S01-08, S01-12
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Minimal workflow topology
  Scenario: A run flows from ingest through triage to the downstream stub
    Given the DE specimen submitted to the workflow
    When the workflow executes
    Then the ingest executor runs first
    And the triage agent runs second, receiving the ingest output
    And a downstream stub executor receives the triage output
```

Done means: this topology is the one S01-09's agnosticism proof runs against, and the one S01-16 extends with the real DocControl agent.

---

### [ ] S01-14 — Triage agent returns a `DocumentProfile`

As the pipeline
I want a Haiku-tier triage agent that profiles a document into type, jurisdiction, doc reference, version, layout family and confidence
So that downstream routing (layout-family prompt variants, unknown-document handling) has something to key off (design §5.3)

Depends on: S01-13
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Triage agent
  Scenario: DE specimen is profiled correctly
    Given the DE specimen's document handle
    When the triage agent runs
    Then it returns a DocumentProfile with JurisdictionIso "DE"
    And DocType identifies it as a cross-border trading legal instruction
    And Confidence is reported as a value between 0 and 1
  Scenario: CH specimen is profiled correctly
    Given the CH specimen's document handle
    When the triage agent runs
    Then it returns a DocumentProfile with JurisdictionIso "CH"
```

Done means: `DocumentProfile` matches the record shape in design Appendix A exactly (`DocType, JurisdictionIso, DocRef, Version, LayoutFamily, Confidence`).

---

### [ ] S01-15 — Triage routes low-confidence or unknown documents to review

As the pipeline
I want triage's conditional edge to route a document to the review queue instead of guessing when confidence is low or the document type is unrecognised
So that unknown layouts never proceed silently into extraction (design §4, §9 "New/unknown layout family" row)

Depends on: S01-14
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Triage low-confidence routing
  Scenario: An unrecognised document is routed to review, not extraction
    Given a document that is not a recognisable cross-border trading legal instruction
    When triage runs and returns a DocumentProfile with confidence below the routing threshold
    Then the workflow routes to the review-queue stub
    And no section agent runs for this document
```

Done means: review-queue stub is sufficient for this story (a queue table row); the full HITL pause/resume mechanism is built in Sprint 03 — this story only needs the routing edge to fire correctly.

---

### [ ] S01-16 — DocControl agent extracts end-to-end from the DE specimen

As the pipeline
I want the DocControl section agent to extract the document-control block from the DE specimen into a `DocControlSection`, with every scalar wrapped in the `ExtractedField<T>` provenance envelope
So that the first full extraction slice (ingest → triage → one section agent) is proven against a real specimen (design §5.4)

Depends on: S01-09, S01-14
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: DocControl agent end-to-end
  Scenario: DocControl section extracted from the DE specimen
    Given the DE specimen's document handle and DocumentProfile from triage
    When the DocControl agent runs
    Then it returns a DocControlSection with a non-null DocRef and Version
    And every scalar field is wrapped in ExtractedField<T> with SourcePage and SourceSnippet populated
    And every SourceSnippet appears verbatim on its reported SourcePage of the PDFPig text layer
```

Done means: this is the first agent whose output is later fed to the validator (Sprint 02) — its `ExtractedField<T>` shape must match design §5.4 exactly so the grounding gate can consume it unchanged.

---

### [ ] S01-17 — Anthropic API key sourced from the secrets-manager pattern

As an operator
I want the Anthropic API key read from the bank-standard secrets manager pattern, never from config files or baked into images
So that CBIX meets the design's security baseline from the first commit that calls the API (design §8 Security and data governance)

Depends on: S01-08
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Secret sourcing
  Scenario: API key is not present in configuration or source
    Given the worker's configuration files and Docker image layers
    When I search them for the literal Anthropic API key value
    Then no match is found
  Scenario: Worker resolves the key at startup from the secrets provider
    Given a secrets-manager-compatible provider configured for local/test use
    When the worker starts
    Then the Anthropic client is constructed with a key resolved from that provider, not from appsettings
```

Done means: the local/test secrets provider used in CI is documented as a stand-in for the bank's Vault/CyberArk pattern, not the production mechanism itself.
