using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Cida.Desktop;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipe;
    public bool IsOwner { get; }
    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        if (Environment.GetEnvironmentVariable("CIDA_PROFILE") is { Length: > 0 } profile)
            sid += "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(profile)))).Substring(0, 12);
        _pipe = "cida-open-" + sid;
        _mutex = new Mutex(true, "Local\\Cida-" + sid, out var created);
        IsOwner = created;
    }
    public async Task NotifyAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(1500); using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync("open");
        }
        catch (Exception e) when (e is IOException or TimeoutException) { }
    }
    public void Listen(Action open)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    using var reader = new StreamReader(server);
                    if (await reader.ReadLineAsync(deadline.Token) == "open") _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(open);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { await Task.Delay(200); }
            }
        });
    }
    public void Dispose()
    { _stop.Cancel(); if (IsOwner) _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
