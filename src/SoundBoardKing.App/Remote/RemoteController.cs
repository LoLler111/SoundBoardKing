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

    public Task<bool> StopOneAsync(Guid playbackId) => dispatcher.InvokeAsync(() => viewModel.StopPlayback(playbackId)).Task;

    public Task TogglePauseAsync() => dispatcher.InvokeAsync(() => viewModel.TogglePauseCommand.Execute(null)).Task;

    public Task<bool> TogglePauseOneAsync(Guid playbackId) =>
        dispatcher.InvokeAsync(() => viewModel.TogglePausePlayback(playbackId)).Task;

    public Task SetMicEnabledAsync(bool enabled) => dispatcher.InvokeAsync(() => viewModel.PlayOnMic = enabled).Task;

    public Task SetEffectAsync(SoundEffect effect) => dispatcher.InvokeAsync(() => viewModel.SoundEffect = effect).Task;

    public Task SetEffectAmountAsync(int percent) => dispatcher.InvokeAsync(() => viewModel.EffectAmount = percent).Task;

    public Task SetPlaybackSpeedAsync(double speed) =>
        dispatcher.InvokeAsync(() => viewModel.PadSpeed = Math.Log2(speed) * 100).Task;

    public Task<bool> RenameSoundAsync(Guid soundId, string title) =>
        dispatcher.InvokeAsync(() => viewModel.RenameSoundById(soundId, title)).Task;

    public Task SetEffectTargetAsync(EffectTarget target) => dispatcher.InvokeAsync(() => viewModel.EffectTarget = target).Task;
}
