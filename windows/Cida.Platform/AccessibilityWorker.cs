using System.Diagnostics;
using System.Text.Json;

namespace Cida.Platform;

public sealed record AccessibilityRequest(string Operation, long Window, int X = 0, int Y = 0)
{
    public ReplacementSnapshot? Snapshot { get; init; }
    public string? Expected { get; init; }
}
public sealed record AccessibilityResponse
{
    public SelectionReader.Result? Selection { get; init; }
    public TranslationLayer.LayerParagraph? Paragraph { get; init; }
    public IReadOnlyList<WindowParagraphReader.Paragraph>? Paragraphs { get; init; }
    public ReplacementSnapshot? Replacement { get; init; }
    public bool Verified { get; init; }
}

/// <summary>Bounded UIA queries in a recyclable child process; timed-out COM calls are terminated.</summary>
public sealed class AccessibilityWorker : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private Process? _process;
    private bool _disposed;
    private readonly string? _executablePath;
    public AccessibilityWorker(string? executablePath = null) => _executablePath = executablePath;
    public async Task<AccessibilityResponse?> QueryAsync(AccessibilityRequest request, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            if (_process == null || _process.HasExited)
            {
                DisposeProcess();
                var path = _executablePath ?? Path.Combine(AppContext.BaseDirectory, "Cida.exe");
                if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "Cida.Cli.exe");
                if (!File.Exists(path)) return null;
                var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("--accessibility-worker");
                _process = Process.Start(info);
                if (_process == null) return null;
                _process.ErrorDataReceived += (_, _) => { }; _process.BeginErrorReadLine();
            }
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            await _process.StandardInput.FlushAsync(deadline.Token);
            var line = await _process.StandardOutput.ReadLineAsync(deadline.Token);
            return line == null ? null : JsonSerializer.Deserialize<AccessibilityResponse>(line);
        }
        catch (OperationCanceledException) { DisposeProcess(); if (token.IsCancellationRequested) throw; return null; }
        catch { DisposeProcess(); return null; }
        finally { _gate.Release(); }
    }
    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) return RunLoop();
        var result = 0;
        var thread = new Thread(() => result = RunLoop()); thread.SetApartmentState(ApartmentState.MTA);
        thread.Start(); thread.Join(); return result;
    }
    private static int RunLoop()
    {
        for (var line = Console.ReadLine(); line != null; line = Console.ReadLine())
        {
            AccessibilityResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<AccessibilityRequest>(line)!;
                response = request.Operation switch
                {
                    "selection" => new() { Selection = new SelectionReader().ReadFromAutomation((nint)request.Window) },
                    "paragraph" => new() { Paragraph = new TranslationLayer().ParagraphAtPoint(request.X, request.Y) },
                    "window" => new() { Paragraphs = new WindowParagraphReader().ReadVisibleParagraphs((nint)request.Window) },
                    "replacement" => new() { Replacement = SelectionReplacer.Capture((nint)request.Window) },
                    "verify-replacement" when request.Snapshot != null && request.Expected != null => new() { Verified = SelectionReplacer.Verify(request.Snapshot, request.Expected) },
                    _ => new(),
                };
            }
            catch { response = new(); }
            Console.WriteLine(JsonSerializer.Serialize(response));
        }
        return 0;
    }
    private void DisposeProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }
    public void Dispose() { _disposed = true; DisposeProcess(); }
}
