Feature: Ingest triggers Claude upload

  The Files API upload and its cache_control setup happen once, at ingest, before triage
  and before any section agent runs (design 5.1). Ingest is the only place that knows a
  submission is new, so it is the only place that can guarantee "once" - and the handle it
  produces is what the rest of the run carries instead of the document.

  The third scenario exists because producing a document block is not the same as sending
  one. Measured in S01-05: MAF's Anthropic integration silently drops an AIContent whose
  only payload is RawRepresentation, so a consumer that merely attaches the prepared
  content gets a run where every agent answers confidently about a document it was never
  shown. The 1h cache TTL adds a second opt-in, the extended-cache-ttl beta, whose
  necessity is derived from the pinned SDK's model rather than from a live call - so the
  offline scenarios pin that we SEND it, and the @requiresApi lane below is what settles
  whether the API demands it. Both obligations are recorded against this story in the plan.

  @wire
  Scenario: Ingest produces a reusable document handle
    Given the registered DE specimen with its PDFPig text layer
    When the ingest executor runs with the Claude profile active
    Then the Claude Files API upload happens exactly once
    And the resulting file_id is attached to the run's document handle for downstream agents

  @wire
  Scenario: A duplicate submission uploads nothing
    Given the DE specimen was already ingested with the Claude profile active
    When the same file is submitted to the ingest executor again unchanged
    Then no further Files API upload is made
    And the duplicate result carries no document handle

  @wire
  Scenario: An agent request carries the prepared document and its cache opt-in
    Given a run whose document handle was produced by ingest
    When a section agent runs against that document
    Then the outbound message request carries the document block referencing the same "file_id"
    And the outbound message request carries the "extended-cache-ttl-2025-04-11" beta

  @requiresApi
  Scenario: The live API accepts a document-carrying request with its beta opt-ins
    Given a live run whose document handle was produced by ingest
    When a section agent runs against that document
    Then the live API accepts the request and answers
