Feature: Claude document content profile

  Document presentation is the one place where providers differ most sharply (design 5.1),
  and the Claude profile's answer is the Files API: upload the PDF once, reference the
  resulting file_id from a document block, and mark that block with cache_control so every
  later agent call hits the prompt cache instead of re-sending the document. Seven section
  agents fan out over one document, so "once" is the whole point - a second upload is not a
  slow path, it is a bug that costs money and invalidates the cache the design depends on.

  The wire scenarios answer the question at the HTTP layer, through a canned transport: what
  the profile promises is a property of the outbound request, and a stub returning an invented
  file_id would satisfy the Gherkin while proving nothing. The @requiresApi variants make the
  same assertions against the real Files API and skip - visibly - when no key is present.

  @wire
  Scenario: DE specimen uploaded once and referenced by file_id
    Given the DE specimen "data/Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf"
    When the Claude profile prepares document content for a first agent call
    Then the PDF is uploaded to the Claude Files API exactly once
    And the returned content block references the resulting "file_id"
    And "cache_control" is set on the document block

  @wire
  Scenario: A second agent call against the same document reuses the file_id
    Given the DE specimen was already uploaded in this run
    When the Claude profile prepares document content for a second agent call
    Then no second upload call is made
    And the same "file_id" is reused

  @requiresApi
  Scenario: The live Files API accepts the DE specimen and returns one file_id
    Given the live Claude Files API and the DE specimen "data/Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf"
    When the Claude profile prepares document content for a first agent call
    Then the PDF is uploaded to the Claude Files API exactly once
    And the returned content block references the resulting "file_id"
    And "cache_control" is set on the document block

  @requiresApi
  Scenario: A second agent call against the live API reuses the file_id
    Given the live Claude Files API already uploaded the DE specimen in this run
    When the Claude profile prepares document content for a second agent call
    Then no second upload call is made
    And the same "file_id" is reused
