using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Cida.Windows.Tests;

internal sealed class ControlledModelServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private int _requests;
    public int Requests => _requests;
    public string Endpoint { get; }
    public ControlledModelServer()
    {
        _listener.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/chat/completions";
        _ = ServeAsync();
    }
    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream(); var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 16384 && await stream.ReadAsync(one, _stop.Token) > 0)
                {
                    header.Add(one[0]); if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var size = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1];
                var body = new byte[Math.Clamp(int.Parse(size), 0, 1000000)]; await stream.ReadExactlyAsync(body, _stop.Token);
                using var json = JsonDocument.Parse(body);
                var text = json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? "";
                var reply = "清楚表达原意。";
                if (text.StartsWith('['))
                {
                    using var paragraphs = JsonDocument.Parse(text);
                    reply = JsonSerializer.Serialize(paragraphs.RootElement.EnumerateArray().Select(p => new { id = p.GetProperty("id").GetInt32(), text = "清楚表达原意。" }));
                }
                Interlocked.Increment(ref _requests);
                var response = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = reply } } } });
                await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: " + response + "\n\ndata: [DONE]\n\n"), _stop.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}
