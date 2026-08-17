Feature: DocControl agent end-to-end

  The first real extraction slice: ingest prepares the document, triage profiles it, and the
  DocControl section agent reads the document-control block out of it into a DocControlSection
  whose every scalar carries design 5.4's provenance envelope - value, source page, verbatim
  source snippet, confidence.

  GROUNDING IS THE POINT OF THIS FEATURE, not a detail of it. Design 5.6 makes verbatim
  containment of every SourceSnippet in the PDFPig text layer the validator's cheapest and most
  effective hallucination defence, and this story is where the shape that gate consumes is
  fixed. So the scenarios assert the containment themselves, ordinally, against the real text
  layer of the real committed specimen - not against a fixture, and not by trusting the agent.

  THE CANNED SNIPPETS ARE REAL. Every snippet in the offline lane's canned reply was taken from
  the actual PDFPig extraction of the DE specimen and verified as an exact ordinal substring of
  the page it names. That is what makes the containment assertion a test of string containment
  rather than a test of a fixture agreeing with itself - and the negative-control scenario is
  what proves the assertion has teeth: one nearly-right snippet, differing in three digits from
  a real one, must be rejected.

  THE @wire SCENARIO DISCHARGES THE STORY'S SECOND DONE-MEANS BULLET. A green extraction with no
  document block on the wire means the model answered from nothing - fluently, plausibly, and
  about a document it was never shown. Measured in S01-05: MAF's Anthropic integration silently
  drops an AIContent whose only payload is RawRepresentation. So the outbound DocControl request
  is inspected for the document block and both beta opt-ins, exactly as triage's is.

  A REFUSED REPLY FAILS THE RUN HERE, and that is deliberate rather than an omission. Triage owns
  the review edge because triage's job is routing; a section agent that returns something which is
  not its contract is a validator problem, and Sprint 02's ValidationReport plus its targeted retry
  edge is what turns one into a re-invocation of just that agent. Building routing for it now would
  be that story done in the wrong place, so the failure stays typed, loud and unrouted.

  Scenario: DocControl section extracted from the DE specimen
    Given the DE specimen's document handle and DocumentProfile from triage
    When the DocControl agent runs
    Then it returns a DocControlSection with a non-null DocRef and Version
    And every scalar field is wrapped in ExtractedField<T> with SourcePage and SourceSnippet populated
    And every SourceSnippet appears verbatim on its reported SourcePage of the PDFPig text layer
    And the run completes with one section extracted

  Scenario: A fabricated snippet is caught by the grounding check
    Given a DocControl reply whose DocRef snippet is not in the document
    When the DocControl agent runs
    Then the grounding check reports the fabricated snippet as ungrounded
    And every other SourceSnippet still appears verbatim on its reported SourcePage

  Scenario: A reply that is not the DocControl contract fails the run, typed
    Given a DocControl reply that is prose rather than the contract
    When the DocControl agent runs
    Then the run fails with a typed section refusal
    And no review-queue row is written for the section failure

  Scenario: A well-formed reply that found nothing does not pass as an extraction
    Given a DocControl reply whose every field is a well-formed null
    When the DocControl agent runs
    Then the run fails with a typed section refusal
    And no review-queue row is written for the section failure

  @wire
  Scenario: The DocControl request carries the prepared document and its cache opt-in
    Given the DE specimen prepared for a Claude-backed DocControl run
    When the DocControl agent runs
    Then the outbound DocControl request carries the document block referencing the uploaded "file_id"
    And the outbound DocControl request carries the "files-api-2025-04-14" beta
    And the outbound DocControl request carries the "extended-cache-ttl-2025-04-11" beta

  @requiresApi
  Scenario: The live pipeline extracts the DocControl block from the DE specimen
    Given the DE specimen prepared for a live Claude-backed DocControl run
    When the DocControl agent runs
    Then it returns a DocControlSection with a non-null DocRef and Version
    And every scalar field is wrapped in ExtractedField<T> with SourcePage and SourceSnippet populated
    And every SourceSnippet appears verbatim on its reported SourcePage of the PDFPig text layer
    And the run completes with one section extracted
