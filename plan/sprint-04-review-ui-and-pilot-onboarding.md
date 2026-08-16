# Sprint 04 — Review UI and pilot onboarding

**Milestone:** M1 — Pilot

**Sprint goal:** Ship a working review UI for the 100%-review pilot phase, close the normalisation-dictionary maintenance loop, and onboard all five pilot jurisdictions with layout-family prompt variants.

**Sprint exit criteria:**
- A reviewer can view a PDF page beside extracted fields, see the source region located by snippet match, and approve/correct/reject.
- An `UNMAPPED` category value surfaced in review, once approved, is written to the dictionary and never asked again for the same raw value.
- All five pilot jurisdictions are onboarded, running under 100% human review, with their current versions backfilled.

---

### [ ] S04-01 — Review UI: PDF page rendered beside extracted fields

As a reviewer
I want to see the source PDF page next to the extracted fields for the section under review
So that I can visually compare the document to what was extracted without switching tools (design §5.7)

Depends on: S03-04
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Review UI page rendering
  Scenario: Reviewer opens a queued item and sees the PDF page
    Given a review-queue item for the DE specimen's Matrix section, page 3
    When the reviewer opens the item in the review UI
    Then page 3 of the DE specimen is rendered in the UI
    And the extracted fields for the Matrix section are displayed alongside it
```

Done means: this is explicitly scoped as "a thin internal tool" per design §5.7/§11, not a general-purpose document viewer.

---

### [ ] S04-02 — Review UI: source region located by snippet text-match

As a reviewer
I want the UI to highlight the approximate source region on the page by matching each field's SourceSnippet against the page's text layer
So that I don't have to hunt for where a value came from (design §5.7, §11 — no cell bounding boxes are available)

Depends on: S04-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Review UI snippet location
  Scenario: A field's source snippet is highlighted on the rendered page
    Given a DocControl field whose SourceSnippet is "Effective Date: 2026-01-15" on page 1
    When the reviewer views this field in the review UI
    Then the region on the rendered page matching "Effective Date: 2026-01-15" is visually highlighted
  Scenario: A snippet with no exact page match shows an explicit "not located" state
    Given a field whose SourceSnippet cannot be matched on its reported page (e.g. due to OCR variance)
    When the reviewer views this field
    Then the UI shows an explicit "source not located" indicator instead of a wrong or missing highlight
```

Done means: matches the design's accepted limitation ("locates sources by snippet text-match rather than coordinates") — this story does not attempt bounding-box precision.

---

### [ ] S04-03 — Review UI: approve/correct/reject wired to the response port

As a reviewer
I want approve, correct and reject actions in the UI that submit a response through the HITL request/response port
So that my review decision resumes the workflow correctly (design §5.7)

Depends on: S04-01, S03-05
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Review UI actions
  Scenario: Approve resumes the workflow unchanged
    Given a reviewer viewing a queued item with no corrections needed
    When the reviewer clicks Approve
    Then a response is submitted through the request/response port
    And the workflow resumes at the persist step with the original values
  Scenario: Correct submits a modified value and captures it
    Given a reviewer changes a field's value in the UI
    When the reviewer submits the correction
    Then the response carries the corrected value
    And the correction-capture flow from S03-06 records the (document, field, model-value, human-value) tuple
  Scenario: Reject routes the document out of the publish path
    Given a reviewer determines the extraction is unusable
    When the reviewer clicks Reject
    Then the run does not proceed to persist
    And the document is flagged for manual follow-up rather than silently dropped
```

Done means: —

---

### [ ] S04-04 — Normalisation dictionary maintenance: UNMAPPED surfaced in review

As a reviewer
I want an UNMAPPED category value to appear in the review UI with the canonical enum options available for selection
So that I can resolve unmapped taxonomy values without leaving the review tool (design §5.5, §9 "Unknown client-category label" row)

Depends on: S04-01, S02-10
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: UNMAPPED review surfacing
  Scenario: An UNMAPPED category is presented with canonical options
    Given a matrix cell whose ClientCategoryCanonical was set to "UNMAPPED" for raw value "Semi-Professional Investor"
    When the reviewer opens this item
    Then the UI presents the full canonical enum as selectable options
    And the raw value "Semi-Professional Investor" is displayed for context
```

Done means: —

---

### [ ] S04-05 — Normalisation dictionary maintenance: approved mapping persists to category_mappings

As the pipeline
I want a reviewer's resolution of an UNMAPPED value written to the category_mappings dictionary
So that the same raw value is never asked again (design §5.5 — "approved mappings are added to the dictionary so the same question is never asked twice")

Depends on: S04-04
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Dictionary maintenance
  Scenario: Reviewer resolution is added to the dictionary
    Given the reviewer maps "Semi-Professional Investor" to canonical "PROFESSIONAL" in the review UI
    When the response is submitted
    Then dbo.category_mappings gains an entry mapping "Semi-Professional Investor" to "PROFESSIONAL"
  Scenario: The same raw value is resolved deterministically next time
    Given "Semi-Professional Investor" -> "PROFESSIONAL" is now in category_mappings
    When a later document contains a matrix cell with ClientCategoryRaw "Semi-Professional Investor"
    Then the normaliser (S02-09's dictionary-first lookup) resolves it without invoking the agent
```

Done means: this closes the loop opened by S02-10; verified by re-running the S02-09-style scenario against the newly added dictionary entry.

---

### [ ] S04-06 — Layout-family prompt variants applied per triage classification

As the pipeline
I want the section-agent prompts to select a layout-family variant based on triage's `LayoutFamily` output
So that pilot jurisdictions with structurally different documents (tables vs bullets, different taxonomy schemes) get appropriately tailored prompts (design §5.3, §10 Phase 1)

Depends on: S01-14, S02-01
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Layout-family prompt variants
  Scenario: A pilot document with a bullet-based layout uses the bullet-family prompt variant
    Given triage classifies a pilot jurisdiction's document with LayoutFamily "bullets-v1"
    When the Matrix section agent runs
    Then the prompt used includes the "bullets-v1" few-shot example, not the default table-based one
  Scenario: An unrecognised layout family falls back to review rather than a wrong variant
    Given triage returns a LayoutFamily with no matching prompt variant configured
    When the workflow selects a prompt variant
    Then the document routes to review (per S01-15) instead of using a mismatched variant
```

Done means: —

---

### [ ] S04-07 — Pilot jurisdiction onboarding, 1 of 5

As the pilot programme owner
I want the first non-specimen pilot jurisdiction's document processed end-to-end under 100% human review
So that the pipeline is validated against a real jurisdiction beyond the DE/CH specimens (design §10 Phase 1)

Depends on: S04-03, S04-06
Size: M

Acceptance criteria (BDD):
```gherkin
Feature: Pilot jurisdiction onboarding - jurisdiction 1
  Scenario: Jurisdiction 1's current document version is processed and reviewed
    Given jurisdiction 1's current document version submitted to the pipeline
    When the run completes extraction and validation
    Then the run reaches review regardless of confidence (100% review policy for the pilot)
    And a reviewer completes the review to either publish or reject
```

Done means: 100% review is enforced by policy configuration, not by coincidentally low confidence — verified by confirming even a high-confidence run still routes to review.

---

### [ ] S04-08 — Pilot jurisdiction onboarding, 2 of 5

As the pilot programme owner
I want the second pilot jurisdiction's document processed end-to-end under 100% human review
So that layout variety across jurisdictions is exercised incrementally

Depends on: S04-07
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Pilot jurisdiction onboarding - jurisdiction 2
  Scenario: Jurisdiction 2's current document version is processed and reviewed
    Given jurisdiction 2's current document version submitted to the pipeline
    When the run completes extraction and validation
    Then the run reaches review under the 100% review policy
    And a reviewer completes the review to either publish or reject
```

Done means: —

---

### [ ] S04-09 — Pilot jurisdiction onboarding, 3 of 5

As the pilot programme owner
I want the third pilot jurisdiction's document processed end-to-end under 100% human review

Depends on: S04-08
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Pilot jurisdiction onboarding - jurisdiction 3
  Scenario: Jurisdiction 3's current document version is processed and reviewed
    Given jurisdiction 3's current document version submitted to the pipeline
    When the run completes extraction and validation
    Then the run reaches review under the 100% review policy
    And a reviewer completes the review to either publish or reject
```

Done means: —

---

### [ ] S04-10 — Pilot jurisdiction onboarding, 4 of 5

As the pilot programme owner
I want the fourth pilot jurisdiction's document processed end-to-end under 100% human review

Depends on: S04-09
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Pilot jurisdiction onboarding - jurisdiction 4
  Scenario: Jurisdiction 4's current document version is processed and reviewed
    Given jurisdiction 4's current document version submitted to the pipeline
    When the run completes extraction and validation
    Then the run reaches review under the 100% review policy
    And a reviewer completes the review to either publish or reject
```

Done means: —

---

### [ ] S04-11 — Pilot jurisdiction onboarding, 5 of 5

As the pilot programme owner
I want the fifth pilot jurisdiction's document processed end-to-end under 100% human review, completing the pilot cohort

Depends on: S04-10
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Pilot jurisdiction onboarding - jurisdiction 5
  Scenario: Jurisdiction 5's current document version is processed and reviewed
    Given jurisdiction 5's current document version submitted to the pipeline
    When the run completes extraction and validation
    Then the run reaches review under the 100% review policy
    And a reviewer completes the review to either publish or reject
  Scenario: All five pilot jurisdictions are now published
    Given jurisdictions 1 through 5 have each completed review and been published
    When I query dbo.country_documents
    Then five distinct country_iso values from the pilot cohort have a current published version
```

Done means: —

---

### [ ] S04-12 — Backfill of five pilot jurisdictions' current versions via the standard path

As the pilot programme owner
I want each pilot jurisdiction's current document version backfilled through the standard per-document workflow path
So that the pilot's published data set is complete from day one (design §10 Phase 1: "backfill of those countries' current versions")

Depends on: S04-11
Size: S

Acceptance criteria (BDD):
```gherkin
Feature: Pilot backfill via standard path
  Scenario: All five current pilot versions are published
    Given the five pilot jurisdictions' current document versions have each completed S04-07 through S04-11
    When I query dbo.country_documents for the five pilot country_iso codes
    Then each has exactly one currently valid (valid_to NULL) published version
```

Done means: this story explicitly uses the standard online path, not the Message Batches API — Batches API backfill for the larger historical estate is built in Sprint 05 and applied at M2, per design §5.9 and this plan's roadmap.
