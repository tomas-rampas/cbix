Feature: Secret sourcing

  The Anthropic API key reaches the worker from the deployment's secrets manager and from
  nowhere else. Design 8 states the production mechanism: "the API key lives in the bank's
  secrets manager under rotation", the house Vault/CyberArk pattern. Locally and in CI that
  manager is stood in for by .NET configuration composed from user-secrets and environment
  variables - a documented stand-in, not the production mechanism itself. What has to hold in
  every environment is the same in all three: no tracked file carries the key, the key is
  resolved through one named port, and a deployment without one fails at startup rather than
  on the first API call - mid-run, after checkpoints, on a document that has already paid for
  other agents' calls.

  Scenario: API key is not present in configuration or source
    Given the worker's tracked configuration and image assets
    When I search them for credential-shaped content
    Then no match is found
    And the same search does find a planted key, so the scan is not vacuous

  Scenario: Worker resolves the key at startup from the secrets provider
    Given a secrets-manager-compatible provider carrying the Anthropic API key
    And an appsettings file that carries no credential
    When the worker composes its services and builds its host
    Then the Anthropic agent factory resolves from the host's service provider
    And composing without that provider fails, so the key came from it and not from appsettings

  Scenario: Startup fails fast when no source carries the key
    Given no configured source carries the Anthropic API key
    When the worker composes its services
    Then startup fails naming every source it consulted
    And the failure message carries no key value
