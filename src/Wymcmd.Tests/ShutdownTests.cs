using System.Threading;
using System.Windows.Threading;
using Wymcmd.Core.Store;
using Xunit;

namespace Wymcmd.Tests;

public class ShutdownTests
{
    /// <summary>
    /// Closing the store used to await its writer through whatever context the caller was on.
    /// From the window that context is the dispatcher, which was already blocked waiting for the
    /// very same call - the app froze with no way out but Task Manager. Anything that shuts the
    /// store down has to finish no matter which thread asked.
    /// </summary>
    [Fact]
    public void Closing_the_store_from_a_dispatcher_thread_finishes()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wymcmd-shutdown-{Guid.NewGuid():N}.db");
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            try
            {
                using var store = new EventStore(file);
                store.Bounds();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "closing the store blocked the dispatcher thread");
        Assert.Null(failure);

        try { File.Delete(file); } catch { /* the pool may still hold it */ }
    }
}
