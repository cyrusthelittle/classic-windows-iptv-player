using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Windows;

public readonly record struct RemoteControlState(int BrowseMode, int MediaKindMode, int ViewMode);

public sealed class RemoteControlService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Action<string>? _commandHandler;
    private Task? _listenTask;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }

    public void Start(int port, Action<string> commandHandler, Func<RemoteControlState> stateProvider)
    {
        Stop();

        Port = Math.Max(1024, Math.Min(65535, port));
        _commandHandler = commandHandler;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        try { _listener.Start(); } catch { Stop(); throw; }
        IsRunning = true;
        // Capture this listener's dependencies: a retired loop must never accept
        // clients from a newer listener or dispatch through its callbacks.
        var listener = _listener;
        var token = _cts.Token;
        _listenTask = ListenLoopAsync(listener, commandHandler, stateProvider, token);
    }

    public void Stop()
    {
        IsRunning = false;

        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        _listener = null;
        _commandHandler = null;
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
    }

    private async Task ListenLoopAsync(TcpListener listener, Action<string> handler, Func<RemoteControlState> provider, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = HandleClientAsync(client, handler, provider, token);
            }
            catch when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (token.IsCancellationRequested) return;
                try { await Task.Delay(300, token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, Action<string> handler, Func<RemoteControlState> provider, CancellationToken token)
    {
        try
        {
            using (client)
            {
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                token = deadline.Token;
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), token).ConfigureAwait(false);
                    if (count == 0) return;
                    read += count;
                    if (Encoding.UTF8.GetString(buffer, 0, read).Contains("\r\n\r\n")) break;
                }
                if (!Encoding.UTF8.GetString(buffer, 0, read).Contains("\r\n\r\n")) return;
                if (read <= 0) return;

                var request = Encoding.UTF8.GetString(buffer, 0, read);
                var firstLine = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0] != "GET")
                { await WriteResponseAsync(stream, "GET required", "text/plain", token, "405 Method Not Allowed"); return; }
                var path = parts[1];

                if (path.Split('?')[0] == "/cmd" || path.StartsWith("/cmd/", StringComparison.Ordinal))
                {
                    var command = ExtractCommand(path);
                    if (!IsCommand(command))
                    { await WriteResponseAsync(stream, "Unknown command", "text/plain", token, "400 Bad Request"); return; }
                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(_commandHandler, handler)) return;
                        handler(command);
                    }

                    await WriteResponseAsync(stream, "{\"ok\":true}", "application/json", token).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, "OK", "text/plain", token).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/state", StringComparison.OrdinalIgnoreCase))
                {
                    var state = provider();
                    var json = $"{{\"browseMode\":{state.BrowseMode},\"mediaKindMode\":{state.MediaKindMode},\"viewMode\":{state.ViewMode}}}";
                    await WriteResponseAsync(stream, json, "application/json", token).ConfigureAwait(false);
                    return;
                }

                if (path == "/") await WriteResponseAsync(stream, BuildRemotePage(), "text/html", token).ConfigureAwait(false); else await WriteResponseAsync(stream, "Not found", "text/plain", token, "404 Not Found");
            }
        }
        catch
        {
            // The remote should never affect the player.
        }
    }

    private static bool IsCommand(string command) => command.StartsWith("search:", StringComparison.Ordinal) ||
        command is "media-all" or "media-live" or "media-movies" or "media-series" or "view-all" or "view-favorites" or "view-recent"
        or "browse-folders" or "browse-letters" or "browse-items" or "playpause" or "stop" or "previous" or "next"
        or "fullscreen" or "channels" or "volume-up" or "volume-down" or "mute" or "up" or "down" or "select" or "back";

    private static string ExtractCommand(string path)
    {
        var questionIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (questionIndex >= 0)
        {
            var command = string.Empty;
            string? value = null;
            var query = path[(questionIndex + 1)..];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;

                var key = WebUtility.UrlDecode(kv[0]);
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                    command = WebUtility.UrlDecode(kv[1]).Trim().ToLowerInvariant();
                else if (key.Equals("value", StringComparison.OrdinalIgnoreCase))
                    value = WebUtility.UrlDecode(kv[1]);
            }

            if (!string.IsNullOrWhiteSpace(command))
                return command == "search" ? command + ":" + (value ?? string.Empty) : command;
        }

        var slashParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return slashParts.Length >= 2 ? WebUtility.UrlDecode(slashParts[1]).Trim().ToLowerInvariant() : string.Empty;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string body, string contentType, CancellationToken token, string status = "200 OK")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
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
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#07111f;color:#f0f7ff;font-family:Segoe UI,Arial,sans-serif}.wrap{max-width:520px;margin:0 auto;padding:18px}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.four{grid-template-columns:repeat(4,minmax(0,1fr))}button{min-width:0}@media(max-width:360px){.wrap{padding:10px}.grid{gap:6px}.four{grid-template-columns:repeat(2,minmax(0,1fr))}}.spacer{height:72px}.section-gap{margin-top:18px}button{height:72px;border:1px solid rgba(120,170,255,.25);border-radius:18px;background:rgba(37,72,116,.65);color:#fff;font-size:18px;font-weight:700;box-shadow:0 12px 28px rgba(0,0,0,.25)}button:active{transform:scale(.98);background:#2d81ff}.primary,.filters button.active{background:#1976ff;border-color:#7eb2ff;box-shadow:0 0 0 2px rgba(126,178,255,.22),0 12px 28px rgba(0,0,0,.25)}.filters{margin-bottom:10px}.filters button{height:48px;border-radius:13px;font-size:14px;box-shadow:none}.filters button.active{box-shadow:0 0 0 2px rgba(126,178,255,.22)}.search{width:100%;height:54px;margin-bottom:10px;padding:0 16px;border:1px solid rgba(120,170,255,.35);border-radius:15px;background:#101e31;color:#fff;font:inherit;font-size:17px;outline:none}.search:focus{border-color:#7eb2ff;box-shadow:0 0 0 2px rgba(126,178,255,.22)}
</style>
</head>
<body>
<div class="wrap">
<input id="search" class="search" aria-label="Search channels and media" type="search" placeholder="Search channels and media" autocomplete="off" oninput="queueSearch(this.value)" />
<div class="grid filters">
<button data-group="browse" data-value="0" onclick="filter('browse',0,'browse-folders')">Folders</button>
<button data-group="browse" data-value="1" onclick="filter('browse',1,'browse-letters')">A-Z</button>
<button data-group="browse" data-value="2" onclick="filter('browse',2,'browse-items')">Items</button>
</div>
<div class="grid four filters">
<button data-group="media" data-value="0" onclick="filter('media',0,'media-all')">All media</button>
<button data-group="media" data-value="1" onclick="filter('media',1,'media-live')">Live TV</button>
<button data-group="media" data-value="2" onclick="filter('media',2,'media-movies')">Movies</button>
<button data-group="media" data-value="3" onclick="filter('media',3,'media-series')">Series</button>
</div>
<div class="grid filters">
<button data-group="view" data-value="0" onclick="filter('view',0,'view-all')">All</button>
<button data-group="view" data-value="1" onclick="filter('view',1,'view-favorites')">Favorites</button>
<button data-group="view" data-value="2" onclick="filter('view',2,'view-recent')">Recent</button>
</div>
<div class="grid">
<span class="spacer"></span>
<button onclick="cmd('up')" aria-label="Move up">▲</button>
<span class="spacer"></span>
<button onclick="cmd('previous')">⏮ PREV</button>
<button class="primary" onclick="cmd('select')" aria-label="Open highlighted item">OK</button>
<button onclick="cmd('next')">NEXT ⏭</button>
<button onclick="cmd('volume-down')">VOL −</button>
<button onclick="cmd('down')" aria-label="Move down">▼</button>
<button onclick="cmd('volume-up')">VOL +</button>
</div>
<div class="grid section-gap">
<button onclick="cmd('back')">Back</button>
<button onclick="cmd('playpause')">▶ / ⏸</button>
<button onclick="cmd('channels')">Channels</button>
<button onclick="cmd('stop')">⏹ Stop</button>
<button onclick="cmd('mute')">Mute</button>
<button onclick="cmd('fullscreen')">⛶ Full</button>
</div>
</div>
<script>
let searchTimer, pendingSearch; let commandQueue=Promise.resolve();
function queueSearch(value){pendingSearch=value;clearTimeout(searchTimer);searchTimer=setTimeout(()=>{pendingSearch=undefined;cmd('search',value)},250);}
function setActive(group,value){document.querySelectorAll('[data-group="'+group+'"]').forEach(button=>{const active=Number(button.dataset.value)===value;button.classList.toggle('active',active);button.setAttribute('aria-pressed',active)});}
function filter(group,value,name){cmd(name);}
async function syncState(){try{const response=await fetch('/state',{cache:'no-store'});if(!response.ok)return;const state=await response.json();setActive('browse',state.browseMode);setActive('media',state.mediaKindMode);setActive('view',state.viewMode);}catch(e){}}
function cmd(name,value){if(name!=='search' && pendingSearch!==undefined){clearTimeout(searchTimer);const search=pendingSearch;pendingSearch=undefined;cmd('search',search)}commandQueue=commandQueue.then(()=>sendCommand(name,value));return commandQueue;}
async function sendCommand(name,value){try{let url='/cmd?name='+encodeURIComponent(name);if(value!==undefined)url+='&value='+encodeURIComponent(value);const response=await fetch(url,{cache:'no-store'});if(!response.ok)throw new Error('HTTP '+response.status);setTimeout(syncState,75);}catch(e){alert('Command failed: '+e.message);}}
syncState();setInterval(syncState,750);
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
