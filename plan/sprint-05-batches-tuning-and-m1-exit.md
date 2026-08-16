# Sprint 05 — Batches backfill, tuning and M1 exit

**Milestone:** M1 — Pilot

**Sprint goal:** Build the Message Batches API backfill mechanism, measure per-section correction rates with production drift sampling, tune prompts/tiering/dictionary from real pilot corrections, and produce the M1 exit review.

**Sprint exit criteria:**
- Historical documents can be submitted through the Message Batches API and their results land in the same validated/persisted path as online runs.
- Per-section correction rate is measured and reported from the pilot cohort's review history.
- Drift-monitoring sampling runs continuously against reviewed documents in production.
- At least one prompt, tiering or dictionary change has been made in response to measured corrections, with a before/after eval-harness comparison.
- An M1 exit review document records correction-rate trend and states whether the M2 entry condition (sustained <~2% correction rate for two consecutive review cycles) is met or how many cycles remain.

---

### [ ] S05-01 — Batches API backfill: submission

As an operator
I want historical documents submitted for extraction via the Anthropic Message Batches API instead of the online per-document path
So that backfill runs asynchronously at discounted rates, appropriate for work with no latency requirement (design §5.9, §7)

Depends on: S01-08, S02-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Batches API submission
  Scenario: A batch of historical documents is submitted
    Given a set of historical documents queued for backfill
    When the backfill job runs
    Then each document's section-extraction requests are submitted as part of a single Message Batch
    And the job records the batch identifier for later polling
```

Done means: only the section-agent calls need batching per design §5.9's intent (asynchronous, discounted); ingest/triage/validation/persist remain synchronous code paths that consume batch results once available.

---

### [ ] S05-02 — Batches API backfill: results retrieved and fed into the standard pipeline

As an operator
I want completed batch results polled, retrieved and fed into the same validator/persist path used by online runs
So that backfilled documents get identical quality gates to pilot documents (design §5.9 — "asynchronous, discounted; backfill has no latency requirement")

Depends on: S05-01, S02-18, S02-20
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Batches API result retrieval
  Scenario: Completed batch results flow through validation and persist
    Given a submitted batch has completed processing
    When the backfill job polls and retrieves the batch results
    Then each document's results are passed through the same validator gates as an online run
    And documents passing all gates are published exactly as an online run would be
  Scenario: A batch result requiring review is queued, not silently dropped
    Given a batch result that fails the completeness gate on the matrix cell count
    When the result is processed
    Then it is routed to the review queue, consistent with the online retry/review edges
```

Done means: this mechanism is what M2's outline extends from 5 pilot jurisdictions to the full historical estate.

---

### [ ] S05-03 — Per-section correction-rate measurement

As the pilot programme owner
I want a report of correction rate per section, computed from the golden-set corrections captured during the pilot
So that prompt/tiering tuning decisions (S05-05) are based on measured data, not guesswork (design §10 Phase 1: "Measure correction rate per section")

Depends on: S03-07, S04-11
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Per-section correction rate report
  Scenario: Correction rate is reported for each of the seven sections
    Given the pilot cohort's review history with captured corrections across all five jurisdictions
    When the correction-rate report runs
    Then it reports a correction rate percentage for each of DocControl, Entities, Matrix, Conditions, Marketing, Tax and VersionHistory
    And sections are ranked from highest to lowest correction rate
```

Done means: —

---

### [ ] S05-04 — Drift-monitoring sampling in production

As an operator
I want the four golden-set metrics sampled continuously against reviewed production documents, not just in CI
So that quality drift is detected in production before it becomes a systemic problem (design §5.9 — "the same metrics are sampled continuously against reviewed documents to detect drift")

Depends on: S03-10, S02-22
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Production drift monitoring
  Scenario: Reviewed production documents feed the drift sample
    Given a pilot document that completed review this cycle
    When the drift-monitoring job runs
    Then the reviewed document's corrected values are compared against the model's original output
    And the same four metrics (field exact-match, matrix cell accuracy, condition-linkage F1, normalisation accuracy) are recorded for this cycle
  Scenario: A drift alert fires on a metric regression
    Given the current cycle's matrix cell accuracy is materially below the trailing average
    When the drift-monitoring job evaluates the cycle
    Then an alert is raised naming the regressed metric
```

Done means: this is explicitly the "sampled continuously" production counterpart to Sprint 02's CI-only golden-set gate — it does not replace S02-23's CI gate.

---

### [ ] S05-05 — Prompt and tier tuning from measured corrections

As the pilot programme owner
I want at least one prompt or model-tier change made in direct response to S05-03's correction-rate findings, verified by a before/after eval-harness run
So that tiering decisions are driven by evidence, per design §5.4's "eval harness decides it, not dogma"

Depends on: S05-03, S02-23
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Evidence-driven prompt and tier tuning
  Scenario: A high-correction-rate section's prompt is revised and re-measured
    Given S05-03 reports the Marketing section has the pilot's highest correction rate
    When the Marketing agent's prompt is revised to address the observed correction pattern
    Then a before/after eval-harness run shows Marketing's field exact-match improved or is unchanged with no regression elsewhere
  Scenario: A section is escalated in model tier if accuracy still lags after prompt tuning
    Given Haiku-tier field accuracy for a section remains below the required threshold after prompt tuning
    When the section's model tier is escalated to Sonnet
    Then the eval-harness run confirms the escalation improves that section's accuracy
    And no other section's configuration changed as part of this run
```

Done means: change is committed with the eval-harness before/after numbers in the commit message or linked report, consistent with design §11's "provider capability drift... a profile swap is treated as a change requiring a green regression run" principle applied here to prompt/tier changes.

---

### [ ] S05-06 — Normalisation dictionary tuning from pilot corrections

As the pilot programme owner
I want the category_mappings dictionary reviewed and extended based on UNMAPPED resolutions accumulated during the pilot
So that the dictionary's coverage reflects real pilot-jurisdiction taxonomy variety, not just the DE/CH specimens (design §5.5, §10 Phase 1)

Depends on: S04-05, S04-11
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Dictionary tuning from pilot corrections
  Scenario: Pilot UNMAPPED resolutions are consolidated into the dictionary
    Given the five pilot jurisdictions produced a combined set of UNMAPPED resolutions during review
    When the dictionary tuning pass runs
    Then dbo.category_mappings contains an entry for every raw category value resolved during the pilot
    And re-running the normaliser against the pilot documents produces zero new UNMAPPED values for previously-seen raw values
```

Done means: —

---

### [ ] S05-07 — M1 exit review

As the milestone owner
I want a compiled M1 exit review reporting the correction-rate trend across review cycles and stating readiness against the M2 entry condition
So that the M0→M1→M2 progression decision is based on recorded evidence, per design §10 Phase 1/2

Depends on: S05-03, S05-04, S05-05, S05-06
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: M1 exit review
  Scenario: M1 exit review reports correction-rate trend and M2 readiness
    Given at least two consecutive review cycles of pilot data with correction-rate measurements from S05-03 and S05-04
    When the M1 exit review is compiled
    Then it reports the sustained correction rate trend across those cycles
    And it explicitly states whether the trend is below the ~2% M2 entry threshold for two consecutive cycles
    And if the threshold is not yet met, it states how many more cycles are estimated before it likely will be
```

Done means: this review's output feeds directly into `plan/00-roadmap.md`'s M2 entry-condition check; the roadmap is updated with a link or summary once this review is complete.
