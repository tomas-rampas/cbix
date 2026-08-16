Feature: Ingest text layer extraction

  Ingest prepares two representations of every document it registers (design 5.1). This is
  the first: a local text layer extracted with PDFPig, which costs nothing per call and is
  the corpus the validator's grounding gate checks snippets against in Sprint 02 (design
  5.6 - "every SourceSnippet must appear verbatim in the PDFPig text layer").

  Grounding is a string-containment check, so the useful property of this text layer is not
  that it is pretty but that it is faithful: whatever the extractor does to the page's
  characters, a snippet an agent copied out of the same page has to still be findable in
  the result. That is what makes the second assertion below - retrieval by logical page
  number - load-bearing rather than cosmetic: the gate looks on the page the agent named.

  Scenario: Text layer extracted per page for the DE specimen
    Given the registered DE specimen
    When the ingest executor extracts the text layer
    Then a text string is produced for every page in the document
    And the per-page text is retrievable by logical page number

  Scenario: A duplicate submission does not re-extract the text layer
    Dedupe short-circuits before document preparation (design 5.1): the registry decides
    whether there is any work left to do, and a re-submission stops there. Extraction
    running anyway would be work paid for on a run that does not continue - and, once the
    Files API upload joins this step in S01-12, work paid for in provider tokens.

    Given the DE specimen was already registered with its text layer extracted
    When the same file is submitted for ingest again unchanged
    Then no second text layer is extracted
    And the duplicate result carries no text layer
