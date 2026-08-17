# Sprint 03 — Checkpointing, human-in-the-loop and observability

**Milestone:** M1 — Pilot

**Entry note:** M1 stories assume data-governance sign-off for real documents has been obtained (design §10 Phase 1 entry condition). Sprint 03 itself does not depend on real documents — it can be built and tested against the synthetic specimens — but the milestone as a whole is gated on that sign-off before pilot documents are processed in Sprint 04.

**Sprint goal:** Make runs durable across crashes and reviewer pauses, capture every human correction into the golden-set flywheel, and make the pipeline observable and cost-bounded in production.

**Sprint exit criteria:**
- A crashed run resumes from its last superstep without repeating completed LLM calls.
- A run paused for human review survives a process restart and resumes correctly on reviewer response.
- Every review correction is captured as a (document, field, model-value, human-value) tuple and appended to the golden set.
- A per-run token budget aborts runaway loops.
- OTel traces (per-executor spans) and the core metrics set are exported.

---

### [ ] S03-01 — SQL Server-backed CheckpointStorage provider

As the pipeline
I want MAF's checkpoint storage backed by a SQL Server provider
So that superstep state survives process restarts (design §5.2, §7)

Depends on: S02-01 (fan-out topology must exist to have multiple supersteps worth checkpointing)
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: SQL Server checkpoint storage
  Scenario: A superstep checkpoint is persisted to SQL Server
    Given a workflow run for the DE specimen in progress
    When the fan-out superstep completes
    Then a checkpoint record is written to the SQL Server-backed CheckpointStorage
    And the checkpoint contains enough state to resume after the fan-in barrier
```

Done means: thin custom `CheckpointStorage` provider implemented if none ships for SQL Server out of the box, per design §5.2.

Security obligations carried forward from Sprint 01 reviews (binding on this story):
- Checkpoint rows will contain the full PDFPig text layer — client document content in production. Encryption-at-rest and a retention policy on the checkpoint store are part of this story's scope, not a later discovery (raised by the S01-11 security review; `DocumentIngestResult` was shaped for run-state carriage on this understanding).
- The ingest refusal security events (EventIds 1010–1015 family) must be routed to operational logging/SIEM when the workflow host wires logging providers — refusals are the instrument that monitors the write-restricted-ingest-root deployment assumption (design §11 addendum).
- `ISecretResolver` is registered container-wide; when agent executors join this container, scope credential-read access deliberately (raised by the S01-17 security review; recorded on the interface).
- **Stuck-document recovery (S01-12 code review):** an ingest-time content-preparation failure leaves a registered, audited document that `IngestAsync` can never prepare again (dedupe short-circuits, no run record exists, so the review queue never sees it). Manual registry intervention is the only path until `extraction_runs` explicitly covers ingest-time preparation failures — this sprint's run-state stories must close that or re-record it.
- **Checkpoint-store integrity constraint (S01-12 security review):** the grounding gate is the compensating control for a tampered `DocumentContentHandle.ProviderToken` — but only while the PDFPig text layer is NOT read back from the same store the token is. If checkpoint rows carry both the token and the text layer, an attacker who can write one can write both and grounding passes on forged data. The checkpoint design must either keep the grounding corpus out of the tamperable store (re-derive from the registered file) or integrity-protect the pair together. Decide explicitly in S03-01.

---

### [ ] S03-02 — Resume-after-crash: no repeated LLM calls

As an operator
I want a crashed worker process to resume an in-flight run from its last checkpoint on restart
So that completed LLM calls are never re-paid for or re-executed (design §5.2, §9 "Process crash mid-run" row)

Depends on: S03-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Resume after crash
  Scenario: Run resumes after simulated crash with no duplicate agent calls
    Given a DE specimen run that has completed ingest, triage and all seven section agents, checkpointed before the normaliser step
    When the worker process is killed and restarted
    Then the run resumes at the normaliser step
    And none of the seven section agents are invoked again
```

Done means: verified by asserting agent-call counts before and after the simulated crash, not just that the run eventually completes.

---

### [ ] S03-03 — HITL request/response port pauses the workflow at zero compute cost

As the pipeline
I want MAF's request/response port to checkpoint and idle a run when it needs human review
So that a paused run costs nothing while waiting for a reviewer (design §5.7)

Depends on: S03-01, S01-15
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: HITL pause
  Scenario: A run needing review checkpoints and idles
    Given a DE specimen run that exhausted retries on the Matrix gate
    When the workflow reaches the review edge
    Then the workflow checkpoints its state via the request/response port
    And the run makes no further LLM calls while awaiting reviewer response
```

Done means: —

---

### [ ] S03-04 — Review queue table receives review items

As a reviewer
I want a review queue table populated with the document, offending section, and validator findings whenever a run pauses for review
So that reviewers have a concrete worklist without inspecting workflow internals (design §5.7, §6 `review_queue`)

Depends on: S03-03
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Review queue population
  Scenario: A paused run creates a review queue item
    Given a run paused for review due to Matrix gate exhaustion
    When the pause takes effect
    Then a row is created in dbo.review_queue referencing the document, doc_version, offending section and the ValidationReport
```

Done means: queue schema follows the operational-tables pattern named in design §6.

---

### [ ] S03-05 — Review response resumes the workflow at the persist step

As the pipeline
I want a reviewer's response delivered through the request/response port to resume the workflow directly at the persist step
So that approved or corrected review outcomes flow straight to publication without re-running extraction (design §5.7, §5.2's `AddEdge(review, persist)`)

Depends on: S03-04
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Review response resumes workflow
  Scenario: An approved review resumes to persist
    Given a paused run awaiting review, and the reviewer approves all fields as-is
    When the review response is submitted
    Then the workflow resumes and proceeds directly to the persist executor
    And no section agent re-runs as part of this resume
```

Done means: —

---

### [ ] S03-06 — Correction capture: (document, field, model-value, human-value) tuple

As the pipeline
I want every reviewer correction stored as a structured tuple identifying the document, field, the model's original value and the human's corrected value
So that corrections are queryable and attributable (design §5.7)

Depends on: S03-05
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Correction tuple capture
  Scenario: A corrected field is captured as a tuple
    Given a paused review where the reviewer changes StatusCode from "P" to "PR" for a specific matrix cell
    When the review response is submitted
    Then a correction record is stored capturing the document reference, the field identifier, model-value "P" and human-value "PR"
```

Done means: —

---

### [ ] S03-07 — Corrections appended to the golden set automatically

As the milestone owner
I want every captured correction appended to the golden set
So that review is the improvement flywheel described in design §5.7, not just a safety net

Depends on: S03-06
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Golden set flywheel
  Scenario: A correction is added to the golden set
    Given a correction tuple was just captured for a DE specimen matrix cell
    When the correction is persisted
    Then the golden set (dbo.golden_set) gains a corresponding entry reflecting the human-corrected value
    And the next eval harness run (Sprint 02's harness) includes this entry in its comparison set
```

Done means: golden-set growth is verified by an eval-harness run before/after showing the new entry counted.

---

### [ ] S03-08 — Token-budget middleware aborts runaway loops

As an operator
I want a per-run token budget enforced in agent middleware that aborts the run, alerts and dead-letters it when exceeded
So that a cost-runaway agent loop cannot run indefinitely (design §8 Performance and cost, §9 "Cost runaway" row)

Depends on: S01-08
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Token budget middleware
  Scenario: A run under budget completes normally
    Given a run whose cumulative token usage stays under the configured per-run budget
    When the run completes
    Then no abort occurs
  Scenario: A run exceeding budget is aborted and dead-lettered
    Given a run whose cumulative token usage crosses the configured per-run budget mid-execution
    When the next agent call would be made
    Then the middleware aborts the run instead of making the call
    And an alert is raised
    And the run is moved to a dead-letter state
```

Done means: budget value is configurable, not hardcoded, so pilot tuning (Sprint 05) can adjust it.

---

### [ ] S03-09 — OTel traces: per-executor spans exported

As an operator
I want MAF's OpenTelemetry traces exported with one span per executor per run
So that a run's timeline is visible in the bank's APM/Elasticsearch stack (design §8 Observability)

Depends on: S02-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: OTel executor spans
  Scenario: A run produces one span per executor
    Given a completed DE specimen run through ingest, triage, all section agents, normaliser, validator and persist
    When I inspect the exported trace for this run
    Then each executor that ran has a corresponding span
    And spans are correctly nested/ordered to reflect the workflow topology
```

Done means: exporter configuration targets an OTLP-compatible collector, matching "existing APM/Elasticsearch stack" without hardcoding a specific vendor SDK beyond OTel's standard exporters.

---

### [ ] S03-10 — OTel metrics: tokens, cache-hit rate, confidence, validation failures, retries, queue depth

As an operator
I want custom OTel metrics covering tokens/cost per document, cache-hit rate, per-section confidence distributions, validation failure rate by rule, retry counts and review-queue depth
So that the operational picture described in design §8 is visible without querying the database directly

Depends on: S03-09, S02-22
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: OTel custom metrics
  Scenario: Core metrics are recorded for a completed run
    Given a completed DE specimen run that hit the prompt cache on agents two through seven and passed all gates on the first attempt
    When I inspect the exported metrics for this run
    Then tokens and estimated cost for the document are recorded
    And the cache-hit rate reflects that six of seven section agents hit cache
    And a per-section confidence value is recorded for each of the seven sections
  Scenario: Validation failure and retry metrics are recorded
    Given a run where the Matrix gate failed once and then passed on retry
    When I inspect the exported metrics
    Then a validation-failure-by-rule counter is incremented for the completeness rule
    And a retry-count metric of 1 is recorded for the Matrix section
  Scenario: Review-queue depth is observable
    Given two runs are currently paused awaiting review
    When I inspect the review-queue-depth gauge
    Then it reports 2
```

Done means: these are the same metric families S05-04's drift-monitoring sampling will read from in production.
