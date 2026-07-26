using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Windows;

public sealed class RemoteControlService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Action<string>? _commandHandler;
    private Task? _listenTask;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }

    public void Start(int port, Action<string> commandHandler)
    {
        Stop();

        Port = Math.Max(1024, Math.Min(65535, port));
        _commandHandler = commandHandler;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        IsRunning = true;
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        IsRunning = false;

        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_listener is null) return;
                var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await Task.Delay(300, token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0) return;

                var request = Encoding.UTF8.GetString(buffer, 0, read);
                var firstLine = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = parts.Length >= 2 ? parts[1] : "/";

                if (path.StartsWith("/cmd", StringComparison.OrdinalIgnoreCase))
                {
                    var command = ExtractCommand(path);
                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        _commandHandler?.Invoke(command);
                    }

                    await WriteResponseAsync(stream, "{\"ok\":true}", "application/json", token).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, "OK", "text/plain", token).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, BuildRemotePage(), "text/html", token).ConfigureAwait(false);
            }
        }
        catch
        {
            // The remote should never affect the player.
        }
    }

    private static string ExtractCommand(string path)
    {
        var questionIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (questionIndex >= 0)
        {
            var query = path[(questionIndex + 1)..];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    return WebUtility.UrlDecode(kv[1]).Trim().ToLowerInvariant();
                }
            }
        }

        var slashParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return slashParts.Length >= 2 ? WebUtility.UrlDecode(slashParts[1]).Trim().ToLowerInvariant() : string.Empty;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string body, string contentType, CancellationToken token)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);
        await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), token).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), token).ConfigureAwait(false);
    }

    public static IReadOnlyList<string> GetLocalUrls(int port)
    {
        var urls = new List<string> { $"http://localhost:{port}/" };

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var address in host.AddressList)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(address)) continue;
                urls.Add($"http://{address}:{port}/");
            }
        }
        catch
        {
            // localhost is still useful.
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildRemotePage()
    {
        return """
<!doctype html>
<html>
<head>
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Classic Windows IPTV Player Remote</title>
<style>
:root{color-scheme:dark}body{margin:0;background:#07111f;color:#f0f7ff;font-family:Segoe UI,Arial,sans-serif}.wrap{max-width:520px;margin:0 auto;padding:18px}h1{font-size:24px;margin:8px 0 2px}.hint{color:#9db3cc;margin:0 0 18px}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.wide{grid-column:span 3}.two{grid-column:span 2}button{height:72px;border:1px solid rgba(120,170,255,.25);border-radius:18px;background:rgba(37,72,116,.65);color:#fff;font-size:18px;font-weight:700;box-shadow:0 12px 28px rgba(0,0,0,.25)}button:active{transform:scale(.98);background:#2d81ff}.primary{background:#1976ff}.small{height:56px;font-size:15px}.footer{margin-top:16px;color:#8ea6c3;font-size:13px;line-height:1.45}
</style>
</head>
<body>
<div class="wrap">
<h1>Classic Windows IPTV Player Remote</h1>
<p class="hint">Use this from your phone or any device on the same network.</p>
<div class="grid">
<button onclick="cmd('up')">▲</button>
<button class="primary" onclick="cmd('select')">OK / Play</button>
<button onclick="cmd('back')">Back</button>
<button onclick="cmd('previous')">⏮ Prev</button>
<button onclick="cmd('playpause')">▶ / ⏸</button>
<button onclick="cmd('next')">Next ⏭</button>
<button onclick="cmd('down')">▼</button>
<button onclick="cmd('stop')">⏹ Stop</button>
<button onclick="cmd('fullscreen')">⛶ Full</button>
<button class="small" onclick="cmd('channels')">Channels</button>
<button class="small" onclick="cmd('volume-down')">Vol -</button>
<button class="small" onclick="cmd('volume-up')">Vol +</button>
<button class="wide small" onclick="cmd('mute')">Mute / Unmute</button>
</div>
<div class="footer">If the phone cannot connect, allow Classic Windows IPTV Player through Windows Firewall for Private networks.</div>
</div>
<script>
async function cmd(name){try{await fetch('/cmd?name='+encodeURIComponent(name),{cache:'no-store'});}catch(e){alert('Command failed: '+e.message);}}
</script>
</body>
</html>
""";
    }

    public void Dispose()
    {
        Stop();
    }
}
