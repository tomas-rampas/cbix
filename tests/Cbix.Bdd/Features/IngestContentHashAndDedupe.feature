Feature: Ingest content hash and dedupe

  Ingest is code, never an agent (design 5.1). Before a single LLM call is paid for, the
  incoming document is hashed and matched against the registry: bytes nobody has submitted
  before are registered under that hash, and a re-submission of the same bytes is a no-op
  that leaves an audit-log entry (design 9, "Duplicate submission" row).

  The hash is also the identity everything downstream keys on, so the registry is the sole
  authority that derives a document id from the bytes it actually read (design 8,
  Idempotency). The MAF executor that wraps this arrives in S01-13; these scenarios drive
  the ingest service that executor will call.

  Scenario: First submission is registered
    Given the DE specimen has not been submitted before
    When the ingest executor processes it
    Then a content hash is computed
    And a new registry record is created with that hash

  Scenario: Duplicate submission is a no-op
    Given the DE specimen was already registered with its content hash
    When the same file is submitted again unchanged
    Then the ingest executor makes no new registry record
    And an audit log entry records the duplicate

  Scenario: A submission that escapes the ingest root is refused
    Ingest owns the containment half of the document trust boundary: DocumentReference
    enforces shape and delegates containment to "the registry / ingest layer". Without
    this check, a path pointing at a private key or a credentials file is a perfectly
    well-formed reference that a later stage would happily upload to a third party.

    Given the DE specimen is in the configured ingest root
    When a path that resolves outside the ingest root is submitted
    Then the submission is refused as outside the ingest root
    And no registry record is created
    And no audit log entry is written
