Feature: Text-only document content profile

  Some provider or configuration will eventually have no vision support at all. That run
  is still a run - it can extract a document's control block, its entities, its conditions
  and its dates perfectly well from text. What it cannot do is read the permission matrix
  reliably, because a matrix is a table and a table is geometry: which status code belongs
  to which product and client category is carried by the row and column a cell sits in,
  not by the order characters come out of a text extractor.

  So the danger this profile guards against is not failure, it is silence - a text-only run
  being counted alongside a full-fidelity one and its matrix accuracy averaged in as though
  the two were the same measurement (design 5.1, 11). The flag below is what makes the
  difference legible.

  The last scenario is why the invariant lives in the capability type rather than in this
  profile's constructor call. A profile that can only send text is degraded by definition,
  and "by definition" has to mean the type refuses the other combination - not that this
  profile remembers to pass the right pair of booleans.

  Scenario: Text-only content is flagged as degraded
    Given the DE specimen's PDFPig text layer
    When the text-only profile prepares document content
    Then the content contains no image blocks
    And the returned capability flag reports "visual content: false"
    And the run is marked degraded because the capability type refuses any other answer
