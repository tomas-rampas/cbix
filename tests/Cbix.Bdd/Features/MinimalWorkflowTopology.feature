Feature: Minimal workflow topology

  The graph MAF actually runs (design 5.2), reduced to its spine: ingest, triage, one
  downstream section slot, and an explicit terminal persist step. Later sprints widen the
  middle - the seven-way section fan-out, the fan-in barrier, the normaliser, the real
  validator - and none of them re-lay the spine.

  Everything here runs offline. Triage's agent is an ordinary AIAgent over a canned
  IChatClient, and the document is presented by the text-only profile, so the whole run
  is exercised with no provider SDK anywhere on the path. That is deliberate: this is the
  topology S01-09's agnosticism proof runs against, and a scenario that needed a provider
  to prove provider-independence would prove nothing.

  Scenario: A run flows from ingest through triage to the downstream stub
    Given the DE specimen submitted to the workflow
    When the workflow executes
    Then the ingest executor runs first
    And the triage agent runs second, receiving the ingest output
    And a downstream stub executor receives the triage output
    And the run completes at the persist step

  Scenario: The graph is composed without naming a provider
    Given the workflow composed from Cbix.Core against a canned chat client
    When I inspect the assemblies the composed graph was built from
    Then no provider adapter assembly took part in composing it

  Scenario: A duplicate submission never reaches the triage agent
    Given the DE specimen was already ingested by an earlier run
    When the same document is submitted to the workflow again in a new run
    Then the triage agent is not called
    And the run completes with the duplicate-ignored disposition
