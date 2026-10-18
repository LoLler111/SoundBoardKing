using NAudio.Dsp;
using NAudio.Wave;

namespace SoundBoardKing.Audio;

/// <summary>
/// Passes audio through unchanged while measuring its spectrum, for the meters next to the volume
/// sliders. Runs on the audio thread; <see cref="Read(float[])"/> can be called from any thread.
/// </summary>
public sealed class SpectrumTap : ISampleProvider
{
    public const int BandCount = 24;

    private const int FftSize = 1024;
    private const int FftExponent = 10;
    private const float MinFrequency = 40f;
    private const float MaxFrequency = 16_000f;
    private const float FloorDb = -54f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly (int First, int Last)[] _bandBins = new (int, int)[BandCount];
    private readonly float[] _bands = new float[BandCount];
    private readonly object _gate = new();
    private int _filled;
    private long _analysedAt;

    public SpectrumTap(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;

        // Bands are spaced logarithmically, like hearing: as many for 40-160 Hz as for 4-16 kHz.
        float binWidth = (float)source.WaveFormat.SampleRate / FftSize;
        for (int band = 0; band < BandCount; band++)
        {
            float low = MinFrequency * MathF.Pow(MaxFrequency / MinFrequency, (float)band / BandCount);
            float high = MinFrequency * MathF.Pow(MaxFrequency / MinFrequency, (float)(band + 1) / BandCount);
            int first = Math.Clamp((int)(low / binWidth), 1, FftSize / 2 - 1);
            int last = Math.Clamp((int)(high / binWidth), first, FftSize / 2 - 1);
            _bandBins[band] = (first, last);
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        for (int i = offset; i + _channels <= offset + read; i += _channels)
        {
            float mono = 0;
            for (int c = 0; c < _channels; c++)
                mono += buffer[i + c];
            _window[_filled++] = mono / _channels;
            if (_filled == FftSize)
            {
                Analyse();
                _filled = 0;
            }
        }
        return read;
    }

    /// <summary>
    /// Copies the latest levels, 0..1 per band from low to high frequencies; zeros when nothing has
    /// flowed lately (e.g. no app is using the microphone), so the meter does not freeze.
    /// </summary>
    public void Read(float[] levels)
    {
        lock (_gate)
        {
            if (Environment.TickCount64 - _analysedAt > 250)
                Array.Clear(levels);
            else
                Array.Copy(_bands, levels, Math.Min(levels.Length, BandCount));
        }
    }

    private void Analyse()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _window[i] * (float)FastFourierTransform.HannWindow(i, FftSize);
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftExponent, _fft);

        lock (_gate)
        {
            for (int band = 0; band < BandCount; band++)
            {
                var (first, last) = _bandBins[band];
                float peak = 0;
                for (int bin = first; bin <= last; bin++)
                {
                    float magnitude = MathF.Sqrt(_fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y);
                    peak = Math.Max(peak, magnitude);
                }
                // The FFT scales by 1/N and the Hann window halves the amplitude: x4 brings a
                // full-scale sine back to about 1 (0 dB).
                float db = 20 * MathF.Log10(peak * 4 + 1e-9f);
                _bands[band] = Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
            }
            _analysedAt = Environment.TickCount64;
        }
    }
}
