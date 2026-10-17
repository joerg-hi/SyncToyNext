using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace SyncToyNext.App;

public partial class AboutWindow : Window
{
    private const string ProjectUrl = "https://github.com/joerg-hi/SyncToyNext";

    public AboutWindow(string pairsPath, string logPath)
    {
        InitializeComponent();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        VersionText.Text = $"Version {version}";
        PairsPathText.Text = pairsPath;
        LogPathText.Text = logPath;
    }

    private void Website_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
}
