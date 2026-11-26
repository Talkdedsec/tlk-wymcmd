using System.Threading;
using System.Windows.Threading;
using Wymcmd.Core.Model;
using Wymcmd.ViewModels;
using Xunit;

namespace Wymcmd.Tests;

public class WindowCatchUpTests
{
    /// <summary>
    /// The window's model is built before the application starts pumping messages, so nothing
    /// brings an await back to the interface thread by itself. What was recorded while the window
    /// was closed is read on a pool thread; the list it lands in belongs to the window's thread
    /// and throws when touched from another. This builds the model the way the app does - on an
    /// STA thread with no synchronization context - and waits for the caught-up launch to appear.
    /// </summary>
    [Fact]
    public void What_was_recorded_while_closed_shows_up_in_the_list_after_opening()
    {
        const int pid = 930_001;
        var appeared = false;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var model = new MainViewModel((store, _, _) =>
                {
                    store.Enqueue(new ProcEvent
                    {
                        Pid = pid,
                        ImageName = "cmd.exe",
                        ImagePath = @"C:\Windows\System32\cmd.exe",
                        CommandLine = "cmd.exe /c before-logon",
                        StartTime = DateTime.Now.AddMinutes(-5),
                        Sources = EvidenceSource.BlackBox
                    });
                    return 1;
                });

                // What the list control does when it binds: from here on the collection may only
                // be changed from this thread, exactly as in the real window.
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(model.Events);
                view.Refresh();

                var frame = new DispatcherFrame();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                poll.Tick += (_, _) =>
                {
                    appeared = model.Events.Any(evt => evt.Pid == pid);
                    if (appeared || DateTime.UtcNow > deadline) frame.Continue = false;
                };
                poll.Start();

                Dispatcher.PushFrame(frame);
                poll.Stop();
                model.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "the window model never finished");
        Assert.Null(failure);
        Assert.True(appeared, "the caught-up launch never reached the list");
    }
}
