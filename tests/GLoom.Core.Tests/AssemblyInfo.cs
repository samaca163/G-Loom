using Xunit;

// GLoomLog.Sink and GLoomRepository's caches are static by design - the plugin runs
// them from the UI thread. Tests are the only place that assumption is false, and a
// flaky suite is worse than a slow one; the whole suite runs in seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
