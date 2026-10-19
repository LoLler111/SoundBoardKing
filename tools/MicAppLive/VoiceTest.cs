using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoardKing.Audio.MicFx;

/// <summary>
/// "--voce": checks the effect on the voice itself. A 440 Hz tone is played into the cable's
/// input as the "voice"; the robot (55 Hz ring modulation) moves its energy to 385 and 495 Hz.
/// The capture is measured with the effect off, then on.
/// </summary>
internal static class VoiceTest
{
    public static void Run(string cableName, string outDir)
    {
        using var enumerator = new MMDeviceEnumerator();
        var render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .First(d => d.FriendlyName.Contains(cableName, StringComparison.OrdinalIgnoreCase) && d.FriendlyName.Contains("Input"));
        var mic = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .First(d => d.FriendlyName.Contains(cableName, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"voce finta su: {render.FriendlyName}; microfono: {mic.FriendlyName}");

        using var output = new WasapiOut(render, AudioClientShareMode.Shared, true, 50);
        output.Init(new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.4 });
        output.Play();

        using var recorder = new Recorder(mic, "default");
        recorder.Start();
        using var feed = new ApoMicFeed();
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!feed.IsAvailable && DateTime.UtcNow < deadline)
            Thread.Sleep(100);
        Console.WriteLine($"stato modulo: {feed.GetStatus().State}");

        Thread.Sleep(1500);                       // effect off
        double offStart = 0.5, offEnd = recorder.Seconds;
        using (new Timer(_ => feed.SetVoiceEffect(3, 1f), null, 0, 500))
        {
            Thread.Sleep(300);
            double onStart = recorder.Seconds;
            Thread.Sleep(2000);                   // robot on the voice
            double onEnd = recorder.Seconds;
            feed.SetVoiceEffect(0, 0);
            Thread.Sleep(200);
            recorder.Stop();
            output.Stop();

            var data = recorder.Samples;
            int rate = recorder.SampleRate;
            var (off440, offSide) = Measure(data, rate, offStart, offEnd);
            var (on440, onSide) = Measure(data, rate, onStart, onEnd);
            Console.WriteLine($"senza effetto: 440 Hz {off440:F3}, 385+495 Hz {offSide:F3}");
            Console.WriteLine($"con il robot:  440 Hz {on440:F3}, 385+495 Hz {onSide:F3}");
            bool works = off440 > 0.05 && offSide < off440 * 0.3 && onSide > on440 * 2;
            Console.WriteLine(works ? "RISULTATO: EFFETTO SULLA VOCE ATTIVO" : "RISULTATO: NIENTE");

            var wav = Path.Combine(outDir, "micapp-voce.wav");
            using var writer = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
            writer.WriteSamples(data, 0, data.Length);
            Console.WriteLine($"WAV: {wav}");
        }
    }

    private static (double tone, double sidebands) Measure(float[] data, int rate, double from, double to)
    {
        int start = (int)(from * rate), length = (int)((to - from) * rate);
        length = Math.Min(length, data.Length - start);
        if (length <= rate / 10)
            return (0, 0);
        return (Goertzel(data, start, length, rate, 440),
                Goertzel(data, start, length, rate, 385) + Goertzel(data, start, length, rate, 495));
    }

    private static double Goertzel(float[] data, int start, int length, int rate, double frequency)
    {
        double k = Math.Round(length * frequency / rate);
        double w = 2 * Math.PI * k / length, coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (int i = start; i < start + length; i++)
        {
            double s0 = data[i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return 2 * Math.Sqrt(Math.Max(s1 * s1 + s2 * s2 - coeff * s1 * s2, 0)) / length;
    }
}
