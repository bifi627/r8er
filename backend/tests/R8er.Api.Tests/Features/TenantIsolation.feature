Feature: Tenant isolation
  A tenant's devices are visible only to that tenant. The global query filter,
  not the caller, enforces this. (Checkpoint-3 security-review artifact.)

  Scenario: A tenant cannot see another tenant's device
    Given a signed-in user "alice@home.test"
    And that user's tenant owns a device named "alice-nas"
    When "bob@home.test" signs in and lists devices
    Then the device list is empty

  Scenario: A tenant sees its own device
    Given a signed-in user "carol@home.test"
    And that user's tenant owns a device named "carol-nas"
    When "carol@home.test" lists devices
    Then the device list contains "carol-nas"
