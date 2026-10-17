using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SyncToyNext.App;

public partial class SchedulerInfoWindow : Window
{
    public SchedulerInfoWindow(string? selectedPairName)
    {
        InitializeComponent();

        string cmdPath = Path.Combine(AppContext.BaseDirectory, "SyncToyNextCmd.exe");
        AllCommandBox.Text = $"\"{cmdPath}\" -R";

        if (selectedPairName is null)
        {
            SinglePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SingleLabel.Text = $"Nur das Ordnerpaar „{selectedPairName}“ ausführen:";
            SingleCommandBox.Text = $"\"{cmdPath}\" -R \"{selectedPairName}\"";
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(AllCommandBox.Text);

    private void CopySingle_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(SingleCommandBox.Text);

    private void OpenScheduler_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true });
}
