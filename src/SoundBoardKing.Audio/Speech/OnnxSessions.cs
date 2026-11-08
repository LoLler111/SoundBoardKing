using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// Creates the ONNX Runtime sessions of the voices: on the graphics card through DirectML (any DirectX 12
/// card: AMD, NVIDIA, Intel) when asked and possible, otherwise on the processor.
/// </summary>
internal static partial class OnnxSessions
{
    /// <param name="gpu">Try the graphics card first.</param>
    /// <param name="prepare">Adds to the options before the session is made (e.g. external initializers).</param>
    /// <param name="onGpu">Whether the session runs on the graphics card.</param>
    public static InferenceSession Create(string path, bool gpu, out bool onGpu, Action<SessionOptions>? prepare = null) =>
        Create(options => new InferenceSession(path, options), gpu, out onGpu, prepare);

    public static InferenceSession Create(byte[] model, bool gpu, out bool onGpu, Action<SessionOptions>? prepare = null) =>
        Create(options => new InferenceSession(model, options), gpu, out onGpu, prepare);

    // The fastest graphics card first: on a PC with the processor's own graphics too (most Ryzen and Intel
    // desktops), the first adapter Windows lists may be that one rather than the real card. Then the first
    // adapter, as older DirectML setups expect.
    private static readonly Action<SessionOptions>[] GpuChoices =
    [
        options => options.AppendExecutionProvider("DML", new Dictionary<string, string>
        {
            ["performance_preference"] = "high_performance",
            ["device_filter"] = "gpu",
        }),
        options => options.AppendExecutionProvider_DML(0),
    ];

    private static InferenceSession Create(Func<SessionOptions, InferenceSession> make, bool gpu, out bool onGpu,
        Action<SessionOptions>? prepare)
    {
        if (gpu)
        {
            foreach (var choose in GpuChoices)
            {
                var options = Options(prepare);
                try
                {
                    // DirectML wants its own memory planning and one node at a time.
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    choose(options);
                    var session = make(options);
                    onGpu = true;
                    return session;
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException
                                               or SEHException or BadImageFormatException)
                {
                    // No DirectX 12 card, an old driver, or a graph DirectML refuses: the next choice, then the processor.
                    GpuProblem = Reason(ex);
                }
                finally
                {
                    options.Dispose();
                }
            }
        }
        onGpu = false;
        using var cpu = Options(prepare);
        return make(cpu);
    }

    /// <summary>Why the last attempt to use the graphics card failed, if it did.</summary>
    public static string? GpuProblem { get; private set; }

    /// <summary>Notes why a model that was on the graphics card failed while computing.</summary>
    internal static void Failed(Exception ex) => GpuProblem = Reason(ex);

    // ONNX Runtime's messages start with the error code and the source file and line; the reason is at the end.
    internal static string Reason(Exception ex)
    {
        string message = ex.Message.Trim();
        message = ErrorCode().Replace(message, "");
        var parts = HResult().Split(message);
        message = parts[^1].Trim();
        int scope = message.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0 && message.IndexOf(' ', scope) is var space and > 0)
            message = message[(space + 1)..].Trim();
        return message.Length <= 160 ? message : message[..157] + "…";
    }

    [GeneratedRegex(@"^\[ErrorCode:\w+\]\s*")]
    private static partial Regex ErrorCode();

    // "... tid(2e38) C0262002 The reason." — the HRESULT right before the reason.
    [GeneratedRegex(@"\b[0-9A-F]{8}\s")]
    private static partial Regex HResult();

    private static SessionOptions Options(Action<SessionOptions>? prepare)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        prepare?.Invoke(options);
        return options;
    }
}
