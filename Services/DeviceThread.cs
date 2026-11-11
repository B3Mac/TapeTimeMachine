using System.Windows.Threading;

namespace TapeRescue.Services;

/// <summary>
/// A dedicated STA thread with its own dispatcher. All DirectShow objects live here, so slow
/// AV/C round trips to the camcorder never freeze the UI.
/// </summary>
public sealed class DeviceThread : IDisposable
{
    private readonly Thread _thread;

    public Dispatcher Dispatcher { get; }

    public DeviceThread(string name)
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        Dispatcher = dispatcher!;
    }

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public void Invoke(Action action, TimeSpan timeout) =>
        Dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, timeout);

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
