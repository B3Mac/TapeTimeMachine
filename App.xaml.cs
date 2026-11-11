using System.Windows;
using System.Windows.Threading;
using TapeRescue.Services;

namespace TapeRescue;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        Log.Info("Tape Time Machine starting");
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception", e.Exception);
        MessageBox.Show(
            $"Tape Time Machine hit an error it didn't expect:\n\n{e.Exception.Message}\n\nThe details are saved in {Log.FilePath}",
            "Tape Time Machine", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
