namespace SoundBoardKing.Audio.Editing;

public enum FadeDirection
{
    In,
    Out,
}

/// <summary>How the volume moves during a fade.</summary>
public enum FadeCurve
{
    /// <summary>Straight line.</summary>
    Linear,

    /// <summary>Slow at the ends, quicker in the middle: the gentlest to hear.</summary>
    Smooth,

    /// <summary>Most of the change at the quiet end: a long natural tail on a fade-out.</summary>
    Exponential,

    /// <summary>Most of the change at the loud end: the sound drops away quickly.</summary>
    Logarithmic,
}

/// <summary>
/// The editor's operations. Ranges are frames, <c>start</c> included and <c>end</c> excluded, and are
/// clamped to the clip; every operation returns a new clip and leaves the one given untouched.
/// </summary>
public static class ClipEdits
{
    // Joins are smoothed over this long so a cut does not click.
    private const double JoinSeconds = 0.003;

    /// <summary>Keeps only the range: trims everything before and after it.</summary>
    public static AudioClip Keep(AudioClip clip, int start, int end)
    {
        (start, end) = Clamp(clip, start, end);
        int ch = clip.Channels;
        return new AudioClip(clip.Samples[(start * ch)..(end * ch)], clip.SampleRate, ch);
    }

    /// <summary>Removes the range and joins what is left around it.</summary>
    public static AudioClip Delete(AudioClip clip, int start, int end)
    {
        (start, end) = Clamp(clip, start, end);
        int ch = clip.Channels;
        var samples = new float[(clip.Frames - (end - start)) * ch];
        Array.Copy(clip.Samples, 0, samples, 0, start * ch);
        Array.Copy(clip.Samples, end * ch, samples, start * ch, (clip.Frames - end) * ch);
        var result = new AudioClip(samples, clip.SampleRate, ch);
        SmoothJoin(result, start);
        return result;
    }

    /// <summary>Makes the range silent, keeping its length.</summary>
    public static AudioClip Silence(AudioClip clip, int start, int end)
    {
        (start, end) = Clamp(clip, start, end);
        var samples = (float[])clip.Samples.Clone();
        Array.Clear(samples, start * clip.Channels, (end - start) * clip.Channels);
        var result = new AudioClip(samples, clip.SampleRate, clip.Channels);
        SmoothJoin(result, start);
        SmoothJoin(result, end);
        return result;
    }

    /// <summary>Puts <paramref name="other"/> at frame <paramref name="at"/>, converted to this clip's format.</summary>
    public static AudioClip Insert(AudioClip clip, int at, AudioClip other)
    {
        at = Math.Clamp(at, 0, clip.Frames);
        other = other.ConvertTo(clip.SampleRate, clip.Channels);
        int ch = clip.Channels;
        var samples = new float[clip.Samples.Length + other.Samples.Length];
        Array.Copy(clip.Samples, 0, samples, 0, at * ch);
        Array.Copy(other.Samples, 0, samples, at * ch, other.Samples.Length);
        Array.Copy(clip.Samples, at * ch, samples, at * ch + other.Samples.Length, (clip.Frames - at) * ch);
        var result = new AudioClip(samples, clip.SampleRate, ch);
        SmoothJoin(result, at);
        SmoothJoin(result, at + other.Frames);
        return result;
    }

    /// <summary>Replaces the range with <paramref name="other"/> (paste over a selection).</summary>
    public static AudioClip Replace(AudioClip clip, int start, int end, AudioClip other)
    {
        (start, end) = Clamp(clip, start, end);
        return Insert(start == end ? clip : Delete(clip, start, end), start, other);
    }

    /// <summary>Fades the range in (from silence) or out (to silence).</summary>
    public static AudioClip Fade(AudioClip clip, int start, int end, FadeDirection direction, FadeCurve curve)
    {
        (start, end) = Clamp(clip, start, end);
        var samples = (float[])clip.Samples.Clone();
        int ch = clip.Channels;
        int length = end - start;
        for (int f = 0; f < length; f++)
        {
            double t = length == 1 ? 1 : (double)f / (length - 1);
            float gain = (float)Gain(direction == FadeDirection.In ? t : 1 - t, curve);
            int i = (start + f) * ch;
            for (int c = 0; c < ch; c++)
                samples[i + c] *= gain;
        }
        return new AudioClip(samples, clip.SampleRate, ch);
    }

    /// <summary>Gain of a fade-in at <paramref name="t"/> (0..1); a fade-out mirrors it.</summary>
    public static double Gain(double t, FadeCurve curve) => curve switch
    {
        FadeCurve.Smooth => 0.5 - 0.5 * Math.Cos(Math.PI * t),
        FadeCurve.Exponential => t * t * t,
        FadeCurve.Logarithmic => 1 - (1 - t) * (1 - t) * (1 - t),
        _ => t,
    };

    /// <summary>Changes the volume of the range by <paramref name="db"/> decibels.</summary>
    public static AudioClip Amplify(AudioClip clip, int start, int end, double db)
    {
        (start, end) = Clamp(clip, start, end);
        var samples = (float[])clip.Samples.Clone();
        int ch = clip.Channels;
        float gain = (float)Math.Pow(10, db / 20);
        // Inside the sound the gain glides in and out over a few milliseconds instead of jumping.
        int span = Math.Max(1, (int)(JoinSeconds * clip.SampleRate));
        int rampIn = start > 0 ? Math.Min(span, (end - start) / 2) : 0;
        int rampOut = end < clip.Frames ? Math.Min(span, (end - start) / 2) : 0;
        for (int f = start; f < end; f++)
        {
            float g = gain;
            if (f - start < rampIn)
                g = 1 + (gain - 1) * (f - start) / rampIn;
            else if (end - 1 - f < rampOut)
                g = 1 + (gain - 1) * (end - 1 - f) / rampOut;
            for (int c = 0; c < ch; c++)
                samples[f * ch + c] *= g;
        }
        return new AudioClip(samples, clip.SampleRate, ch);
    }

    /// <summary>Highest sample of the range in dBFS; null for digital silence.</summary>
    public static double? PeakDb(AudioClip clip, int start, int end)
    {
        (start, end) = Clamp(clip, start, end);
        float peak = 0;
        for (int i = start * clip.Channels; i < end * clip.Channels; i++)
            peak = Math.Max(peak, Math.Abs(clip.Samples[i]));
        return peak > 0 ? 20 * Math.Log10(peak) : null;
    }

    /// <summary>Brings the loudest sample of the range to <paramref name="targetDb"/> dBFS.</summary>
    public static AudioClip Normalize(AudioClip clip, int start, int end, double targetDb = -1)
    {
        return PeakDb(clip, start, end) is double peak ? Amplify(clip, start, end, targetDb - peak) : clip;
    }

    /// <summary>
    /// Softens a hard edge at <paramref name="frame"/>: a few milliseconds before it fade out and a few
    /// after fade in, so a jump in the waveform does not click. Changes the clip in place (a fresh one).
    /// </summary>
    private static void SmoothJoin(AudioClip clip, int frame)
    {
        if (frame <= 0 || frame >= clip.Frames)
            return;
        int ch = clip.Channels;
        int span = Math.Max(1, (int)(JoinSeconds * clip.SampleRate));
        // Only where the waveform actually jumps: a join inside continuous sound is left alone.
        float jump = 0;
        for (int c = 0; c < ch; c++)
            jump = Math.Max(jump, Math.Abs(clip.Samples[frame * ch + c] - clip.Samples[(frame - 1) * ch + c]));
        if (jump < 0.02f)
            return;
        int before = Math.Min(span, frame);
        int after = Math.Min(span, clip.Frames - frame);
        for (int f = 0; f < before; f++)
        {
            float gain = (float)f / before;
            for (int c = 0; c < ch; c++)
                clip.Samples[(frame - 1 - f) * ch + c] *= gain;
        }
        for (int f = 0; f < after; f++)
        {
            float gain = (float)f / after;
            for (int c = 0; c < ch; c++)
                clip.Samples[(frame + f) * ch + c] *= gain;
        }
    }

    private static (int Start, int End) Clamp(AudioClip clip, int start, int end)
    {
        start = Math.Clamp(start, 0, clip.Frames);
        end = Math.Clamp(end, 0, clip.Frames);
        return start <= end ? (start, end) : (end, start);
    }
}
