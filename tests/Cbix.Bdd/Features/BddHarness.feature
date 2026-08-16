Feature: BDD harness

  This feature is the template every later CBIX story copies. The working agreement
  is: write the scenario first, run it and watch it fail for the right reason
  (an undefined or unsatisfied step, never a compile error), then make it pass.

  Scenario: Sample scenario
    Given the BDD harness is wired to xUnit
    When the sample scenario runs
    Then every step resolves to a binding
