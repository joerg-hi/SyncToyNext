using System.Windows;
using System.Windows.Threading;

namespace SyncToyNext.App;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Ein unerwarteter Fehler ist aufgetreten:\n\n{e.Exception.Message}",
            "SyncToyNext",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
