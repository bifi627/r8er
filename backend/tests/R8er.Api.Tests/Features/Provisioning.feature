Feature: First sign-in provisioning
  A new Firebase identity gets exactly one tenant and one user.

  Scenario: First sign-in creates one tenant and one user
    When a new user "fresh@home.test" signs in
    Then there is exactly 1 tenant and 1 user
