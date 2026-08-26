using Xunit;

// SurveySchemaLoader caches in static fields by design - Grasshopper solves on the UI
// thread, so it needs no synchronisation there. Tests are the only place that assumption
// is false, and a flaky suite is worse than a slow one. These run in well under a second.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
