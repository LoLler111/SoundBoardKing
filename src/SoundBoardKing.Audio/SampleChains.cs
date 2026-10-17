using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundBoardKing.Audio;

internal static class SampleChains
{
    /// <summary>Resamples and remaps channels so <paramref name="source"/> matches a mixer's format.</summary>
    public static ISampleProvider ToFormat(ISampleProvider source, WaveFormat target)
    {
        if (source.WaveFormat.SampleRate != target.SampleRate)
            source = new WdlResamplingSampleProvider(source, target.SampleRate);

        int from = source.WaveFormat.Channels;
        int to = target.Channels;
        if (from == to)
            return source;
        if (from == 1 && to == 2)
            return new MonoToStereoSampleProvider(source);
        if (from == 2 && to == 1)
            return new StereoToMonoSampleProvider(source);
        if (from == 1)
            return new ChannelMapSampleProvider(new MonoToStereoSampleProvider(source), to);
        return new ChannelMapSampleProvider(source, to);
    }
}

/// <summary>Copies the first channels across and pads extra output channels with silence.</summary>
internal sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _buffer = [];

    public ChannelMapSampleProvider(ISampleProvider source, int channels)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int inChannels = _source.WaveFormat.Channels;
        int outChannels = WaveFormat.Channels;
        int frames = count / outChannels;
        int needed = frames * inChannels;
        if (_buffer.Length < needed)
            _buffer = new float[needed];

        int framesRead = _source.Read(_buffer, 0, needed) / inChannels;
        int shared = Math.Min(inChannels, outChannels);
        for (int frame = 0; frame < framesRead; frame++)
        {
            int outBase = offset + frame * outChannels;
            int inBase = frame * inChannels;
            for (int ch = 0; ch < outChannels; ch++)
                buffer[outBase + ch] = ch < shared ? _buffer[inBase + ch] : 0f;
        }
        return framesRead * outChannels;
    }
}
