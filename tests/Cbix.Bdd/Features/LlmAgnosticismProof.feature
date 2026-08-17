Feature: LLM agnosticism proof

  Design 3 makes LLM-agnosticism a hard requirement and then names its own acceptance test:
  "the CI/BDD suite runs the entire workflow against a stub IChatClient with no Anthropic
  dependency on the path, and that run is the acceptance criterion for agnosticism". This
  feature is that run.

  PERMANENCE, and why no CI wiring accompanies this file. `.github/workflows/ci.yml` runs
  `dotnet test Cbix.sln` on every push to every branch and on every pull request into main,
  and that one command runs the whole BDD assembly. A scenario is therefore a permanent gate
  by existing, not by being named in a pipeline step - so story S01-09's "this scenario runs
  in CI on every push, not just Sprint 01" is already satisfied, and a bespoke step for it
  would only create a second place to keep in step with the suite. The roadmap's
  design-component coverage table books the workflow topology (design 5.2) and the provider
  abstraction to Sprint 01; this story is what makes the agnosticism half of that booking
  checkable instead of asserted.

  WHAT THE SECOND ASSERTION IS ABOUT, because it is the difference between a gate and
  theatre. The claim is the story's own and it is about THE RUN: no assembly reachable from
  the dependency graph of this run is `Microsoft.Agents.AI.Anthropic`. The closure is walked
  transitively from the graph Cbix.Core built, from the executors MAF bound into it, from
  every service the run was composed out of, and from the concrete types the run resolved -
  the stub chat client among them. It is NOT the claim that this test project has no
  Anthropic reference: Cbix.Bdd project-references Cbix.Providers.Anthropic permanently,
  because story S01-08's scenarios test the adapter.

  A measured detail that could otherwise be mistaken for the gate working. Cbix.Bdd's
  EMITTED reference table names only Cbix.Core, not the adapter, because the S01-08 bindings
  resolve adapter types by reflection. So a walk rooted here would find no provider today -
  by accident, and only until the first binding that names an adapter type. That accident is
  precisely why the stub chat client and the stub-backed composition live in their own
  provider-free assembly, `Cbix.Agnosticism`: the run executes the stub, so the stub's
  assembly is a root, and being provider-free must be a structural property of that assembly
  rather than a side effect of how some other scenario happens to be written.

  The second scenario is the control that keeps the first honest. It roots the same walk at
  the worker host - the one place in the solution that deliberately chooses a provider - and
  requires the walk to FIND Microsoft.Agents.AI.Anthropic, transitively, through the adapter.
  A walk that reported nothing anywhere would satisfy the first scenario perfectly while
  proving nothing at all. The pairing also states the principle exactly: a host picks a
  provider, a pipeline does not.

  WHAT THIS PROVES AND WHAT IT DOES NOT. The walk reads assembly reference tables, so what it
  establishes is that no assembly on the run's path NAMES a provider type. It cannot see a
  provider reached by Assembly.Load on a string plus Activator, by reflective member lookup,
  or by a plugin path read from configuration - none of those emits a reference. That is the
  same caveat ProviderContainmentTests records for its own member-reference scan, accepted for
  the same reason: the accident being guarded against is somebody wiring a provider into the
  pipeline because it was convenient, and that accident always emits a reference.

  Scenario: Workflow completes end-to-end against a stub chat client
    Given a stub "IChatClient" implementation that returns canned structured-output JSON
    And the workflow is configured with the stub client instead of the Anthropic adapter
    When the workflow runs against a test document
    Then the run completes to the persist step
    And no assembly reachable from this run's dependency graph is "Microsoft.Agents.AI.Anthropic"

  Scenario: The reachability walk finds a provider assembly when one really is reachable
    Given the same reachability walk rooted at the worker host, which does choose a provider
    When the reachability walk runs
    Then "Microsoft.Agents.AI.Anthropic" is reported as reachable
    And it was reached through the provider adapter rather than as a direct reference
