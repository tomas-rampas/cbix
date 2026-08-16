Feature: Generic vision document content profile

  The Claude native-PDF profile is the default, not the only one. A provider without a
  native PDF mode still has to be able to read the permission matrix, which is a visual
  table: extracted text alone loses the row/column geometry that says which status code
  belongs to which product and client category. So the agnosticism fallback (design 5.1)
  presents the document the way any multimodal model understands it - the locally
  extracted PDFPig text layer plus locally rendered page images, as ordinary content
  blocks.

  Both halves are load-bearing and neither substitutes for the other. The images carry
  the layout; the text is what the validator's grounding gate does verbatim containment
  against in Sprint 02, so the text this profile sends has to be the same text the gate
  will later check against, character for character.

  The last assertion is the point of the whole port: this profile is the one that must
  work when the Claude profile cannot, so it may not reach for the Files API or any
  Anthropic type to do its job.

  Scenario: Document content is built from local text and rendered images
    Given the DE specimen and its PDFPig-extracted text layer
    When the generic-vision profile prepares document content
    Then the content includes the extracted text
    And the content includes one rendered image per page
    And no Files API or Anthropic-specific type is referenced anywhere in this profile
