Feature: Tenant isolation
  A tenant's gateways are visible only to that tenant. The global query filter,
  not the caller, enforces this. (Checkpoint-3 security-review artifact.)

  Scenario: A tenant cannot see another tenant's gateway
    Given a signed-in user "alice@home.test"
    And that user's tenant owns a gateway named "alice-nas"
    When "bob@home.test" signs in and lists gateways
    Then the gateway list is empty

  Scenario: A tenant sees its own gateway
    Given a signed-in user "carol@home.test"
    And that user's tenant owns a gateway named "carol-nas"
    When "carol@home.test" lists gateways
    Then the gateway list contains "carol-nas"
