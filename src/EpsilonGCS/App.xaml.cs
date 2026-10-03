using System.Windows;
using System.Windows.Threading;
using EpsilonGCS.Services;

namespace EpsilonGCS;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Gcs.Initialize();
        AppLog.Write("Epsilon GCS starting");

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("Unhandled: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("Unobserved task exception: " + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("UI exception: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "Epsilon GCS - error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Gcs.Shutdown();
        base.OnExit(e);
    }
}
