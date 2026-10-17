using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SyncToyNext.Core;
using SyncToyNext.Core.Model;

namespace SyncToyNext.App;

/// <summary>
/// Anlegen und Bearbeiten eines Ordnerpaars einschließlich aller Optionen.
/// </summary>
public partial class FolderPairWindow : Window
{
    private readonly FolderPair? _original;
    private readonly IEnumerable<FolderPair> _allPairs;

    /// <param name="original">Zu bearbeitendes Paar oder <c>null</c> für ein neues.</param>
    /// <param name="template">Vorbelegung für ein neues Paar (z. B. beim Duplizieren oder nach Drag &amp; Drop).</param>
    public FolderPairWindow(FolderPair? original, IEnumerable<FolderPair> allPairs, FolderPair? template = null)
    {
        InitializeComponent();
        WindowPlacement.Attach(this, "FolderPair", restorePosition: false);
        _original = original;
        _allPairs = allPairs;

        SynchronizeText.Text = UiHelpers.ActionDescription(SyncAction.Synchronize);
        EchoText.Text = UiHelpers.ActionDescription(SyncAction.Echo);
        ContributeText.Text = UiHelpers.ActionDescription(SyncAction.Contribute);

        var pair = original ?? template ?? new FolderPair();
        Title = original is null ? "Neues Ordnerpaar" : $"Ordnerpaar „{original.Name}“ bearbeiten";
        HeaderText.Text = original is null ? "Neues Ordnerpaar" : "Ordnerpaar bearbeiten";
        OkButton.Content = original is null ? "Anlegen" : "Speichern";

        NameBox.Text = pair.Name;
        LeftBox.Text = pair.LeftPath;
        RightBox.Text = pair.RightPath;
        SynchronizeRadio.IsChecked = pair.Action == SyncAction.Synchronize;
        EchoRadio.IsChecked = pair.Action == SyncAction.Echo;
        ContributeRadio.IsChecked = pair.Action == SyncAction.Contribute;

        var o = pair.Options;
        IncludeBox.Text = o.IncludePatterns;
        ExcludeBox.Text = o.ExcludePatterns;
        foreach (string folder in o.ExcludedSubfolders) SubfolderList.Items.Add(PathUtil.ToDisplay(folder));
        ExcludeReadOnlyBox.IsChecked = o.ExcludeReadOnly;
        ExcludeHiddenBox.IsChecked = o.ExcludeHidden;
        ExcludeSystemBox.IsChecked = o.ExcludeSystem;
        RecycleBox.IsChecked = o.SaveOverwrittenToRecycleBin;
        CheckContentsBox.IsChecked = o.CheckFileContents;
        ActiveForRunAllBox.IsChecked = o.ActiveForRunAll;
        OptionsExpander.IsExpanded = original is not null || template is not null && template.Options.ExcludedSubfolders.Count > 0;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            SuggestName();
        };
    }

    /// <summary>Das neue bzw. geänderte Ordnerpaar nach „OK“.</summary>
    public FolderPair? Result { get; private set; }

    // ------------------------------------------------------------------ Ordner wählen

    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftBox, "Linken Ordner auswählen");

    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightBox, "Rechten Ordner auswählen");

    private void Browse(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FolderName;
            SuggestName();
        }
    }

    private void PathBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolder(e) is not null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void PathBox_PreviewDrop(object sender, DragEventArgs e)
    {
        if (DroppedFolder(e) is { } folder && sender is TextBox box)
        {
            box.Text = folder;
            SuggestName();
        }

        e.Handled = true;
    }

    private static string? DroppedFolder(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths.FirstOrDefault(Directory.Exists) : null;

    /// <summary>Schlägt einen Namen vor, solange noch keiner eingegeben ist.</summary>
    private void SuggestName()
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text)) return;
        string left = UiHelpers.FolderName(LeftBox.Text);
        string right = UiHelpers.FolderName(RightBox.Text);
        if (left.Length > 0 && right.Length > 0) NameBox.Text = $"{left} ↔ {right}";
    }

    // ------------------------------------------------------------------ Unterordner

    private void AddSubfolder_Click(object sender, RoutedEventArgs e)
    {
        string left = LeftBox.Text.Trim();
        string right = RightBox.Text.Trim();
        if (!Directory.Exists(left) && !Directory.Exists(right))
        {
            ShowError("Bitte zuerst einen vorhandenen linken oder rechten Ordner angeben.");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Auszuschließende Unterordner auswählen",
            InitialDirectory = Directory.Exists(left) ? left : right,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (string selected in dialog.FolderNames)
        {
            string? relative = ToRelative(selected, left) ?? ToRelative(selected, right);
            if (relative is null)
            {
                ShowError($"\"{selected}\" liegt weder im linken noch im rechten Ordner.");
                continue;
            }

            string display = PathUtil.ToDisplay(relative);
            if (!SubfolderList.Items.Cast<string>().Any(i => string.Equals(i, display, StringComparison.OrdinalIgnoreCase)))
            {
                SubfolderList.Items.Add(display);
            }
        }
    }

    private static string? ToRelative(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == "." || relative.StartsWith("..") || Path.IsPathRooted(relative)) return null;
        return PathUtil.Normalize(relative);
    }

    private void RemoveSubfolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SubfolderList.SelectedItems.Cast<object>().ToList())
        {
            SubfolderList.Items.Remove(item);
        }
    }

    // ------------------------------------------------------------------ Speichern

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.Visibility = Visibility.Collapsed;

        var pair = _original?.Clone() ?? new FolderPair();
        string oldLeft = pair.LeftPath;
        string oldRight = pair.RightPath;

        pair.Name = NameBox.Text.Trim();
        pair.LeftPath = LeftBox.Text.Trim();
        pair.RightPath = RightBox.Text.Trim();
        pair.Action = EchoRadio.IsChecked == true ? SyncAction.Echo
            : ContributeRadio.IsChecked == true ? SyncAction.Contribute
            : SyncAction.Synchronize;

        pair.Options = new PairOptions
        {
            IncludePatterns = string.IsNullOrWhiteSpace(IncludeBox.Text) ? "*" : IncludeBox.Text.Trim(),
            ExcludePatterns = ExcludeBox.Text.Trim(),
            ExcludedSubfolders = SubfolderList.Items.Cast<string>().Select(PathUtil.Normalize).ToList(),
            ExcludeReadOnly = ExcludeReadOnlyBox.IsChecked == true,
            ExcludeHidden = ExcludeHiddenBox.IsChecked == true,
            ExcludeSystem = ExcludeSystemBox.IsChecked == true,
            SaveOverwrittenToRecycleBin = RecycleBox.IsChecked == true,
            CheckFileContents = CheckContentsBox.IsChecked == true,
            ActiveForRunAll = ActiveForRunAllBox.IsChecked == true,
        };

        string? error = SyncEngine.Validate(pair);
        if (error is null && _allPairs.Any(p => p != _original && string.Equals(p.Name, pair.Name, StringComparison.CurrentCultureIgnoreCase)))
        {
            error = $"Es gibt bereits ein Ordnerpaar mit dem Namen „{pair.Name}“.";
        }

        if (error is not null)
        {
            ShowError(error);
            return;
        }

        foreach (string path in new[] { pair.LeftPath, pair.RightPath })
        {
            if (Directory.Exists(path)) continue;

            var create = MessageBox.Show(this,
                $"Der Ordner \"{path}\" existiert nicht.\n\nSoll er angelegt werden?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (create != MessageBoxResult.Yes) return;

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                ShowError($"Der Ordner konnte nicht angelegt werden: {ex.Message}");
                return;
            }
        }

        if (_original is null)
        {
            // Neues Paar (auch beim Duplizieren): eigene Kennung und damit eigene Statusdateien.
            pair.Id = Guid.NewGuid();
            pair.LastRunUtc = null;
        }
        else if (!string.Equals(oldLeft, pair.LeftPath, StringComparison.OrdinalIgnoreCase)
                 || !string.Equals(oldRight, pair.RightPath, StringComparison.OrdinalIgnoreCase))
        {
            // Zeigt das Paar auf andere Ordner, beginnt es mit einem neuen Stand, damit keine
            // Löschungen aus einem alten Stand abgeleitet werden.
            pair.Id = Guid.NewGuid();
            pair.LastRunUtc = null;
        }

        Result = pair;
        DialogResult = true;
    }
}
