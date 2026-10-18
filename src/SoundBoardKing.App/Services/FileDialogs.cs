using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace SoundBoardKing.App.Services;

public static class FileDialogs
{
    private static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Shows a file dialog that still opens when the folder it would start in is gone (a network
    /// share or drive no longer connected): Windows then fails with an error such as 0x80070043,
    /// so it is reopened on the Documents folder.
    /// </summary>
    public static bool Show(FileDialog dialog, Window owner)
    {
        if (!FolderExists(dialog.InitialDirectory))
            dialog.InitialDirectory = DefaultFolder;
        try
        {
            return dialog.ShowDialog(owner) == true;
        }
        catch (COMException)
        {
            // The folder Windows remembers for this dialog can be unreachable too.
            dialog.InitialDirectory = DefaultFolder;
            dialog.FileName = Path.GetFileName(dialog.FileName);
            return dialog.ShowDialog(owner) == true;
        }
    }

    private static bool FolderExists(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return true; // let Windows pick its usual folder
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
