Feature: Triage agent

  Triage is the run's first model call and its only job is routing (design 5.3): a Haiku-tier
  agent reads the prepared document and returns a DocumentProfile - document type, jurisdiction
  ISO, document reference, version, layout family, confidence - in exactly the shape design
  Appendix A declares. Everything after the model's reply is deterministic code: the reply is
  parsed strictly against that contract, and a reply that does not match it is refused rather
  than salvaged. "Agents propose, code disposes" starts here, at the first agent.

  WHAT IS CANNED AND WHAT IS REAL. The offline scenarios replace the model and nothing else.
  The specimen is read off disk through the real ingest gate, hashed, registered and presented
  by the real profile; the reply is canned; and everything downstream of the reply - the strict
  parse, the contract validation, the message the graph carries - is the production code. A
  scenario that also faked the parsing would be asserting that the test agrees with itself.

  THE @wire SCENARIO DISCHARGES A RECORDED OBLIGATION. Measured in S01-05 and recorded against
  S01-12 in the plan: MAF's Anthropic integration silently drops an AIContent whose only payload
  is RawRepresentation, so an agent wired without the attachment seam answers confidently about
  a document it was never shown - a hallucination that reads exactly like an extraction. Triage
  is the first real agent call, so the obligation lands here: the outbound request must carry
  the document block and both beta opt-ins, and that is asserted on the wire rather than argued
  for in prose.

  THE @requiresApi SCENARIOS ARE THE STORY'S ACCEPTANCE EVIDENCE. They spend a document upload
  and a model call each, so they skip visibly - with the reason attached - unless
  ANTHROPIC_API_KEY is set. They are what settles whether the real Haiku tier reads these
  specimens correctly; the canned lanes settle everything that is code.

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

  Scenario: A reply that is prose rather than the contract is refused, never guessed at
    Given a triage agent whose reply to the DE specimen is prose rather than the contract
    When the triage agent runs
    Then no DocumentProfile is produced
    And the refusal names what the reply broke

  Scenario: A reply carrying a field the contract does not declare is refused
    Given a triage agent whose reply to the DE specimen carries an undeclared field
    When the triage agent runs
    Then no DocumentProfile is produced
    And the refusal names what the reply broke

  Scenario: A reply omitting a field the contract declares is refused
    Given a triage agent whose reply to the DE specimen omits a declared field
    When the triage agent runs
    Then no DocumentProfile is produced
    And the refusal names what the reply broke

  Scenario: A confidence outside the unit interval is refused
    Given a triage agent whose reply to the DE specimen reports a confidence above one
    When the triage agent runs
    Then no DocumentProfile is produced
    And the refusal names what the reply broke

  @wire
  Scenario: The triage request carries the prepared document and its cache opt-in
    Given the DE specimen prepared for a Claude-backed triage run
    When the triage agent runs
    Then the outbound triage request carries the document block referencing the uploaded "file_id"
    And the outbound triage request carries the "files-api-2025-04-14" beta
    And the outbound triage request carries the "extended-cache-ttl-2025-04-11" beta
    And it returns a DocumentProfile with JurisdictionIso "DE"

  @requiresApi
  Scenario: The live triage agent profiles the DE specimen
    Given the DE specimen prepared for a live Claude-backed triage run
    When the triage agent runs
    Then it returns a DocumentProfile with JurisdictionIso "DE"
    And DocType identifies it as a cross-border trading legal instruction
    And Confidence is reported as a value between 0 and 1

  @requiresApi
  Scenario: The live triage agent profiles the CH specimen
    Given the CH specimen prepared for a live Claude-backed triage run
    When the triage agent runs
    Then it returns a DocumentProfile with JurisdictionIso "CH"
    And Confidence is reported as a value between 0 and 1
