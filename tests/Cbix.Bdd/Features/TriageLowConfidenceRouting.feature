Feature: Triage low-confidence routing

  Design 9's "New/unknown layout family" row answers a triage confidence below threshold by routing
  the whole document to review rather than guessing at it, and design 5.3 says triage's only job is
  that routing. This feature is the edge that does it: two conditional edges out of the triage node
  where there was one unconditional one, a review-queue node on the low-confidence side, and the
  section agents on the other.

  WHAT "ROUTED TO REVIEW" HAS TO MEAN, precisely, or the gate is theatre. Three things at once: a
  row lands in the review queue saying why, no section agent runs (nobody pays for extracting a
  document nobody recognised), and the run still emits exactly ONE WorkflowOutputEvent carrying its
  disposition. The third is S01-13's invariant and it is binding: a run that ended silently would be
  indistinguishable from one that stalled, so a review branch that simply stopped would trade a
  routing bug for an observability one.

  AN UNPARSEABLE REPLY TAKES THE SAME EDGE, and that is the "never guess" principle realised rather
  than restated. A reply that did not match the DocumentProfile contract is strictly less
  information than a low-confidence one; treating it as a failure instead would put a routine,
  expected outcome on the exception path, where design 9 explicitly does not put it.

  THE THRESHOLD IS CONFIGURATION. Design 11 says model-reported confidence is not to be trusted at
  face value and that thresholds are calibrated against observed correction rates - rates nobody has
  yet. The default is an opening position; the last scenario is what proves moving it needs no build.

  THE QUEUE IS A STUB, deliberately and only here: a row is written, and the full HITL pause and
  resume - MAF's request/response ports over a checkpoint - is Sprint 03. What this story owns is
  that the edge fires and that the row exists to be picked up.

  Scenario: An unrecognised document is routed to review, not extraction
    Given a document that is not a recognisable cross-border trading legal instruction
    When triage runs and returns a DocumentProfile with confidence below the routing threshold
    Then the workflow routes to the review-queue stub
    And no section agent runs for this document
    And the run emits exactly one outcome, queued for review

  Scenario: A reply the contract refuses takes the same edge, rather than failing the run
    Given a document whose triage reply does not match the DocumentProfile contract
    When the workflow runs
    Then the workflow routes to the review-queue stub
    And no section agent runs for this document
    And the queued row says the reply was refused

  Scenario: A recognised document still reaches extraction
    Given a document triage recognises with a confidence above the routing threshold
    When the workflow runs
    Then no review-queue row is written
    And the run reaches the persist step rather than the review queue

  Scenario: The routing threshold is configuration rather than a constant
    Given a deployment whose triage review threshold is configured to 0.9
    And a document triage recognises with a confidence above the routing threshold
    When the workflow runs
    Then the workflow routes to the review-queue stub
    And no section agent runs for this document
