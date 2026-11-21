using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Accord.App;

public static class Program
{
    private const string MutexName = @"Local\Accord.App.SingleInstance";
    private const string ActivateEventName = @"Local\Accord.App.ActivateEvent";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateEvent;

    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using EventWaitHandle evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch
            {
                // Existing instance may be starting up or closing.
            }
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivationListener(_activateEvent);

        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        try { _activateEvent.Dispose(); } catch { }
        try { _singleInstanceMutex.ReleaseMutex(); } catch { }
        _singleInstanceMutex.Dispose();
    }

    private static void StartActivationListener(EventWaitHandle activateEvent)
    {
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!activateEvent.WaitOne()) break;
                    App.MainWindowInstance?.BringToForegroundFromBackground();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Accord.SingleInstanceListener"
        };
        listener.Start();
    }
}
