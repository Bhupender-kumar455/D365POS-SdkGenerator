using System.Windows;
using System.Windows.Threading;

namespace D365SdkGenerator.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply the remembered palette before StartupUri creates the window so a dark-mode
        // user never sees a white flash.
        ThemeService.Apply(ThemeService.Parse(AppPreferences.Load().Theme));

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// The window handles expected failures inline. This is a last-resort guard so a
    /// background continuation can never close the app while a project is half written.
    /// </summary>
    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "D365 Commerce SDK Generator",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
