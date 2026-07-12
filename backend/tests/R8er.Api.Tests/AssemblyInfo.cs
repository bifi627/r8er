using Xunit;

// The whole assembly shares exactly one Postgres + one Firebase Auth emulator
// (see Integration/IntegrationFixture.cs's SharedContainers) across xUnit's
// `[Collection("integration")]` tests and Reqnroll's scenarios. Both share the
// process-global FIREBASE_AUTH_EMULATOR_HOST env var and the database's
// TRUNCATE-based ResetAsync, so collections must not run concurrently — they'd
// race the shared env var and the per-scenario data reset.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
