using SoundBoardKing.App.ViewModels;

namespace SoundBoardKing.App.Services;

public interface IDialogService
{
    void ShowError(string message);
    void ShowInfo(string message);
    bool Confirm(string message);
    bool Confirm(string message, string yes, string no);
    string? PickFolder(string title, string? initialFolder);
    string? OpenSplFile(string? initialFolder);
    IReadOnlyList<string> OpenAudioFiles();
    string? AskText(string title, string prompt, string initialValue = "");

    /// <summary>Returns null when cancelled and an empty string when the user removed the hotkey.</summary>
    string? CaptureHotkey(string title, string? current);

    bool EditSettings(SettingsDialogModel model);
}
