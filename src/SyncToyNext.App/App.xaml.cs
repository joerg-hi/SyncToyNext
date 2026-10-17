using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SyncToyNext.App;

public partial class App : Application
{
    private string? _smokeTestLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Starttest für den automatischen Build: --smoke-test [Protokolldatei] [--screenshots Ordner]
        int index = Array.FindIndex(e.Args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _smokeTestLog = index + 1 < e.Args.Length && !e.Args[index + 1].StartsWith("--")
                ? e.Args[index + 1]
                : Path.Combine(Path.GetTempPath(), "SyncToyNext-smoke.txt");
            int shots = Array.FindIndex(e.Args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunSmokeTest(shots >= 0 && shots + 1 < e.Args.Length ? e.Args[shots + 1] : null);
            return;
        }

        new MainWindow().Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_smokeTestLog is not null)
        {
            FinishSmokeTest(false, e.Exception.ToString());
            return;
        }

        MessageBox.Show(
            $"Ein unerwarteter Fehler ist aufgetreten:\n\n{e.Exception.Message}",
            UiHelpers.AppName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void RunSmokeTest(string? screenshotDir)
    {
        try
        {
            var test = new SmokeTest(screenshotDir);
            bool ok = await test.RunAsync();
            FinishSmokeTest(ok, string.Join(Environment.NewLine, test.Log));
        }
        catch (Exception ex)
        {
            FinishSmokeTest(false, ex.ToString());
        }
    }

    private void FinishSmokeTest(bool success, string details)
    {
        try
        {
            File.WriteAllText(_smokeTestLog!, $"{(success ? "OK" : "FEHLER")}{Environment.NewLine}{details}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }

        Shutdown(success ? 0 : 1);
    }
}
