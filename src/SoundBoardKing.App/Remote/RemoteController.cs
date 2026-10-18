using System.Windows.Threading;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Remote;

namespace SoundBoardKing.App.Remote;

/// <summary>Runs the server's requests on the UI thread, where the view model lives.</summary>
internal sealed class RemoteController(MainViewModel viewModel, Dispatcher dispatcher) : IRemoteController
{
    public long LibraryVersion => viewModel.LibraryVersion;

    public Task<StateDto> GetStateAsync() => dispatcher.InvokeAsync(viewModel.BuildRemoteState).Task;

    public Task<PlayResult> PlayAsync(Guid soundId, PlayTarget target) =>
        dispatcher.InvokeAsync(() => viewModel.PlaySoundById(soundId, target)).Task;

    public Task StopAsync() => dispatcher.InvokeAsync(() => viewModel.StopCommand.Execute(null)).Task;

    public Task TogglePauseAsync() => dispatcher.InvokeAsync(() => viewModel.TogglePauseCommand.Execute(null)).Task;

    public Task SetMicEnabledAsync(bool enabled) => dispatcher.InvokeAsync(() => viewModel.PlayOnMic = enabled).Task;
}
