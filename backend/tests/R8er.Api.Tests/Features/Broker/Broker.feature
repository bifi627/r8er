Feature: Signaling broker
  A browser reaches agents of its own tenant only, and an agent can only answer
  sessions opened for it. The broker relays signaling; it never decides trust.

  Scenario: An offer reaches the tenant's own agent and the answer comes back
    Given "alice@home.test" has an online agent "nas"
    When "alice@home.test" offers to agent "nas"
    Then agent "nas" receives the offer
    When agent "nas" answers that session
    Then the browser receives the answer

  Scenario: A browser cannot reach another tenant's agent
    Given "alice@home.test" has an online agent "nas"
    When "bob@home.test" offers to agent "nas"
    Then the browser is told the agent is offline or unknown
    And agent "nas" receives nothing

  Scenario: An agent cannot answer another agent's session, even knowing its id
    Given "alice@home.test" has an online agent "nas"
    And "bob@home.test" has an online agent "evil"
    When "alice@home.test" offers to agent "nas"
    And agent "nas" receives the offer
    And agent "evil" answers that session
    Then the browser receives nothing

  Scenario: A browser socket without a valid ID token is closed
    When a browser authenticates with token "garbage"
    Then the browser socket is closed
