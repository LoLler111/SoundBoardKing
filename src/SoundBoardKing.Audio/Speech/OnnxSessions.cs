using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// Creates the ONNX Runtime sessions of the voices: on the graphics card through DirectML (any DirectX 12
/// card: AMD, NVIDIA, Intel) when asked and possible, otherwise on the processor.
/// </summary>
internal static class OnnxSessions
{
    /// <param name="gpu">Try the graphics card first.</param>
    /// <param name="prepare">Adds to the options before the session is made (e.g. external initializers).</param>
    /// <param name="onGpu">Whether the session runs on the graphics card.</param>
    public static InferenceSession Create(string path, bool gpu, out bool onGpu, Action<SessionOptions>? prepare = null) =>
        Create(options => new InferenceSession(path, options), gpu, out onGpu, prepare);

    public static InferenceSession Create(byte[] model, bool gpu, out bool onGpu, Action<SessionOptions>? prepare = null) =>
        Create(options => new InferenceSession(model, options), gpu, out onGpu, prepare);

    private static InferenceSession Create(Func<SessionOptions, InferenceSession> make, bool gpu, out bool onGpu,
        Action<SessionOptions>? prepare)
    {
        if (gpu)
        {
            var options = Options(prepare);
            try
            {
                // DirectML wants its own memory planning and one node at a time.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(0);
                var session = make(options);
                onGpu = true;
                return session;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException
                                           or SEHException or BadImageFormatException)
            {
                // No DirectX 12 card, an old driver, or a graph DirectML refuses: the processor does it.
                GpuProblem = ex.Message;
            }
            finally
            {
                options.Dispose();
            }
        }
        onGpu = false;
        using var cpu = Options(prepare);
        return make(cpu);
    }

    /// <summary>Why the last attempt to use the graphics card failed, if it did.</summary>
    public static string? GpuProblem { get; private set; }

    private static SessionOptions Options(Action<SessionOptions>? prepare)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        prepare?.Invoke(options);
        return options;
    }
}
