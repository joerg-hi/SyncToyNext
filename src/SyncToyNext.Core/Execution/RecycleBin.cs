using System.Runtime.InteropServices;

namespace SyncToyNext.Core.Execution;

public interface IRecycleBin
{
    /// <summary>Verschiebt eine Datei in den Papierkorb.</summary>
    void MoveToRecycleBin(string fullPath);
}

/// <summary>
/// Papierkorb von Windows über die Shell-Funktion <c>SHFileOperation</c>.
/// </summary>
/// <remarks>
/// Hinweis: Auf Laufwerken ohne Papierkorb (z. B. Netzwerkfreigaben, viele USB-Sticks) löscht Windows
/// dabei endgültig. Das verhielt sich bei SyncToy genauso.
/// </remarks>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    public void MoveToRecycleBin(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Der Papierkorb wird nur unter Windows unterstützt.");
        }

        // SHFileOperation erwartet eine doppelt nullterminierte Liste; das zweite \0 ergänzt der Marshaller.
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(fullPath) + "\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };

        int result = SHFileOperation(ref op);
        if (result != 0)
        {
            throw new IOException($"Die Datei konnte nicht in den Papierkorb verschoben werden (Fehlercode 0x{result:X}).");
        }

        if (op.fAnyOperationsAborted)
        {
            throw new IOException("Das Verschieben in den Papierkorb wurde abgebrochen.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
