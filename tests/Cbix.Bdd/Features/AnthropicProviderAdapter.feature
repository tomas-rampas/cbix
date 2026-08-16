Feature: Anthropic provider adapter

  The Claude model client is wired through MAF's first-party Anthropic integration so
  that agents in the workflow are ordinary MAF AIAgent instances, indistinguishable
  from one backed by any other provider (design 3, 7). LLM-agnosticism is a hard
  requirement, so the Anthropic types must be confined to a single provider-adapter
  assembly: the second scenario is what makes that containment a fact rather than a
  convention.

  Scenario: An agent built from the adapter is a standard AIAgent
    Given an Anthropic API key configured via the secrets-manager pattern
    When the provider adapter constructs an agent named "docControl" with a model and instructions
    Then the returned object's static type is MAF's "AIAgent"

  Scenario: Anthropic types are confined to the provider adapter
    Given the compiled assemblies of the solution
    When I inspect every non-adapter assembly's referenced assemblies
    Then no code outside "Cbix.Providers.Anthropic" references any type from the "Microsoft.Agents.AI.Anthropic" package
    And "Cbix.Providers.Anthropic" does reference it, so the check is not vacuous
