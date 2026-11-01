Feature: Agent onboarding
  A one-time enrollment token binds a new agent to the token owner's tenant;
  the agent then authenticates with its long-lived credential. Secrets are
  single-use / hashed, and an agent only ever touches its own device row.

  Scenario: An enrollment token works exactly once
    Given "alice@home.test" created an enrollment token
    When an agent enrolls with that token
    Then the enrollment is accepted
    When an agent enrolls with that token
    Then the enrollment is rejected

  Scenario: An expired enrollment token is rejected
    Given "alice@home.test" created an enrollment token
    And the token has expired
    When an agent enrolls with that token
    Then the enrollment is rejected

  Scenario: An enrolled agent belongs to the token owner's tenant only
    Given "alice@home.test" created an enrollment token
    When an agent enrolls with that token
    Then "alice@home.test" sees 1 device
    And "bob@home.test" sees 0 devices

  Scenario: The agent socket rejects unknown credentials
    When an agent connects with credential "garbage"
    Then the agent connection is refused

  Scenario: A heartbeat updates only the sending agent's device
    Given "alice@home.test" has an enrolled agent "nas"
    And "alice@home.test" has an enrolled agent "pc"
    When agent "nas" heartbeats with LAN URL "http://192.168.1.20:8097"
    Then "alice@home.test" sees "nas" online at "http://192.168.1.20:8097/"
    And "alice@home.test" sees "pc" offline
