using SyncToyNext.Core.Model;
using SyncToyNext.Core.Planning;

namespace SyncToyNext.App;

/// <summary>
/// Zeichen der Windows-Symbolschrift (Segoe Fluent Icons / Segoe MDL2 Assets).
/// </summary>
public static class Glyphs
{
    public const string Add = "";
    public const string Edit = "";
    public const string Delete = "";
    public const string Sync = "";
    public const string Forward = "";
    public const string Back = "";
    public const string Copy = "";
    public const string Play = "";
    public const string View = "";
    public const string Folder = "";
    public const string FolderOpen = "";
    public const string NewFolder = "";
    public const string Rename = "";
    public const string Page = "";
    public const string Refresh = "";
    public const string Settings = "";
    public const string Clock = "";
    public const string Document = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Checkmark = "";
    public const string Blocked = "";
    public const string Search = "";
    public const string Filter = "";
    public const string Cancel = "";
    public const string More = "";
    public const string Duplicate = "";

    public static string ForAction(SyncAction action) => action switch
    {
        SyncAction.Synchronize => Sync,
        SyncAction.Echo => Forward,
        SyncAction.Contribute => Copy,
        _ => Sync,
    };

    public static string ForOperation(OperationKind kind) => kind switch
    {
        OperationKind.CreateFolder => NewFolder,
        OperationKind.RenameFolder => Rename,
        OperationKind.Rename => Rename,
        OperationKind.Delete => Delete,
        OperationKind.Copy => Page,
        OperationKind.Overwrite => Refresh,
        OperationKind.DeleteFolder => Delete,
        _ => Page,
    };

    public static string ForStatus(OperationStatus status) => status switch
    {
        OperationStatus.Succeeded => Checkmark,
        OperationStatus.Failed => Error,
        OperationStatus.Skipped => Blocked,
        _ => "",
    };
}
