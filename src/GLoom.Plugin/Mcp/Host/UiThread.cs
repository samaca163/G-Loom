using System;
using System.Threading;
using System.Threading.Tasks;
using Rhino;

namespace GLoom.Mcp.Host;

/// <summary>
/// The one bridge from a listener thread to Rhino's UI thread. Grasshopper documents,
/// the canvas and Eto are UI-thread-only (macOS aborts the process on off-thread access),
/// so every host-touching tool runs through here. Waits are one-directional and bounded:
/// the listener waits on the UI with a timeout, the UI never waits on the listener. An
/// action that has not started when the wait expires is dropped, so it cannot run later
/// against a canvas the tool already reported it could not reach; one already running
/// still finishes, because Grasshopper offers no way to interrupt it.
/// </summary>
public static class UiThread
{
    private static int _uiThreadId = -1;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Call from PriorityLoad, which Grasshopper runs on the UI thread.</summary>
    public static void Initialize() => _uiThreadId = Environment.CurrentManagedThreadId;

    public static bool IsUiThread => Environment.CurrentManagedThreadId == _uiThreadId;

    public static T Run<T>(Func<T> action, TimeSpan? timeout = null)
    {
        if (IsUiThread) return action();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = 0;
        RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            if (Volatile.Read(ref abandoned) == 1) return;
            try { tcs.TrySetResult(action()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));

        // Wait on the handle, not Task.Wait: Task.Wait wraps a faulted result in an
        // AggregateException, which hides every ToolArgumentException the tools raise -
        // the agent would read "one or more errors occurred" instead of what went wrong.
        // GetResult below rethrows the original exception with its type intact.
        if (!((IAsyncResult)tcs.Task).AsyncWaitHandle.WaitOne(timeout ?? DefaultTimeout))
        {
            Volatile.Write(ref abandoned, 1);
            throw new TimeoutException(
                "Rhino's UI thread did not respond in time - a modal dialog or a long solve is probably holding it.");
        }
        return tcs.Task.GetAwaiter().GetResult();
    }

    public static void Run(Action action, TimeSpan? timeout = null) =>
        Run(() => { action(); return true; }, timeout);
}
