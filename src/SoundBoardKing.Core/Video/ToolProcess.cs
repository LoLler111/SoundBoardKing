using System.Diagnostics;
using System.Text;

namespace SoundBoardKing.Core.Video;

/// <summary>What a tool printed and how it ended.</summary>
public sealed record ToolResult(int ExitCode, IReadOnlyList<string> Output, IReadOnlyList<string> Errors)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs one of the downloaded tools (yt-dlp, FFmpeg) hidden, reading what it prints line by line.
/// Cancelling kills it, with whatever it started.
/// </summary>
public static class ToolProcess
{
    /// <summary>Runs <paramref name="exe"/> to the end; <paramref name="onOutput"/> and <paramref name="onError"/> get each line as it comes.</summary>
    public static async Task<ToolResult> RunAsync(string exe, IEnumerable<string> arguments,
        Action<string>? onOutput = null, Action<string>? onError = null, CancellationToken cancel = default)
    {
        using var process = Start(exe, arguments, redirectOutput: true);
        var output = new List<string>();
        var errors = new List<string>();
        var readOutput = ReadLinesAsync(process.StandardOutput, line =>
        {
            lock (output)
                output.Add(line);
            onOutput?.Invoke(line);
        });
        var readErrors = ReadLinesAsync(process.StandardError, line =>
        {
            lock (errors)
            {
                // A long run can print a lot: only the end says what went wrong.
                if (errors.Count > 400)
                    errors.RemoveRange(0, 200);
                errors.Add(line);
            }
            onError?.Invoke(line);
        });
        await WaitAsync(process, cancel);
        await Task.WhenAll(readOutput, readErrors);
        return new ToolResult(process.ExitCode, output, errors);
    }

    /// <summary>
    /// Runs <paramref name="exe"/> and hands its standard output, as bytes, to <paramref name="readOutput"/>
    /// (FFmpeg writing raw samples); the error lines go to <paramref name="onError"/>.
    /// </summary>
    public static async Task<ToolResult> RunBinaryAsync(string exe, IEnumerable<string> arguments,
        Func<Stream, CancellationToken, Task> readOutput, Action<string>? onError = null, CancellationToken cancel = default)
    {
        using var process = Start(exe, arguments, redirectOutput: true);
        var errors = new List<string>();
        var readErrors = ReadLinesAsync(process.StandardError, line =>
        {
            lock (errors)
            {
                if (errors.Count > 400)
                    errors.RemoveRange(0, 200);
                errors.Add(line);
            }
            onError?.Invoke(line);
        });
        try
        {
            await readOutput(process.StandardOutput.BaseStream, cancel);
        }
        catch
        {
            Kill(process);
            throw;
        }
        await WaitAsync(process, cancel);
        await readErrors;
        return new ToolResult(process.ExitCode, [], errors);
    }

    private static Process Start(string exe, IEnumerable<string> arguments, bool redirectOutput)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = redirectOutput ? Encoding.UTF8 : null,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        // yt-dlp is Python: titles with accents or emoji come out right only in UTF-8.
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        return Process.Start(info) ?? throw new InvalidOperationException($"Impossibile avviare {Path.GetFileName(exe)}.");
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        // yt-dlp ends its progress lines with \r when it thinks it writes to a console: both count as an end of line.
        var line = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is '\n' or '\r')
                {
                    if (line.Length > 0)
                        onLine(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }
        if (line.Length > 0)
            onLine(line.ToString());
    }

    private static async Task WaitAsync(Process process, CancellationToken cancel)
    {
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
