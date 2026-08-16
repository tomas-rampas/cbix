Feature: Document content provider port

  Document presentation is the one place where LLM providers differ most sharply, so
  it is isolated behind a single port (design 5.1). The Claude native-PDF profile, the
  generic-vision profile and the text-only profile must all be able to implement it,
  which means nothing provider-specific may appear in its signatures, and a caller must
  be able to tell from the result whether the document was presented visually.

  Scenario: The port exposes only framework-neutral types
    Given the "IDocumentContentProvider" interface in "Cbix.Core"
    When I inspect its method signatures
    Then no method references any Anthropic-specific type
    And the return type carries a capability flag indicating whether visual (image) content is included
