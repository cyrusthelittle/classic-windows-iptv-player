using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;
using System.Net;
using System.Net.Sockets;
using System.Text;

var fixturePath = Path.Combine(Path.GetTempPath(), "cyrus-native-fixture-" + Guid.NewGuid().ToString("N") + ".wav");
try
{
    CreateSilentWave(fixturePath);
    Core.Initialize();
    using var vlc = new LibVLC("--no-video", "--quiet");
    using var tuner = new ChannelTuner(vlc) { MaxAttempts = 1 };
    var states = new List<TunerStatus>();
    var gate = new object();
    tuner.StateChanged += state => { lock (gate) states.Add(state.Status); };
    var request = new TuneRequest("synthetic", "Generated silence", new Uri(fixturePath).AbsoluteUri,
        "local fixture", false, 200);

    for (var i = 0; i < 8; i++)
    {
        tuner.Play(request);
        await Task.Delay(80);
        tuner.Stop();
    }

    tuner.Play(request);
    await WaitUntilAsync(() => { lock (gate) return states.Contains(TunerStatus.Playing); }, TimeSpan.FromSeconds(10));
    tuner.Stop();
    await WaitUntilAsync(() => tuner.CurrentPlayer is null, TimeSpan.FromSeconds(5));
    var countAfterStop = 0;
    lock (gate) countAfterStop = states.Count;
    await Task.Delay(1200);
    lock (gate)
    {
        if (states.Skip(countAfterStop).Any(status => status is TunerStatus.Tuning or TunerStatus.Playing))
            throw new InvalidOperationException("Playback reopened after explicit Stop.");
    }

    var shutdown = Task.Run(() => tuner.Dispose());
    await shutdown.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine("PASS native generated-WAV rapid tune/Stop, no reopen, bounded shutdown");
    await RunStalledHttpFixtureAsync(vlc, File.ReadAllBytes(fixturePath));
}
finally
{
    try { File.Delete(fixturePath); } catch { }
}

static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (!predicate())
    {
        if (DateTime.UtcNow >= until) throw new TimeoutException("Native fixture did not reach expected state.");
        await Task.Delay(50);
    }
}

static void CreateSilentWave(string path)
{
    const int sampleRate = 16_000;
    const int seconds = 15;
    const int sampleCount = sampleRate * seconds;
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8);
    writer.Write(36 + sampleCount * 2);
    writer.Write("WAVEfmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(sampleCount * 2);
    for (var i = 0; i < sampleCount; i++) writer.Write((short)0);
}

static async Task RunStalledHttpFixtureAsync(LibVLC vlc, byte[] wave)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var serverCts = new CancellationTokenSource();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(serverCts.Token);
        using var stream = client.GetStream();
        var request = new byte[4096];
        await stream.ReadAtLeastAsync(request, 1, throwOnEndOfStream: true, serverCts.Token);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length}\r\nConnection: keep-alive\r\n\r\n");
        await stream.WriteAsync(header, serverCts.Token);
        await stream.WriteAsync(wave.AsMemory(0, Math.Min(wave.Length, 160_044)), serverCts.Token);
        await stream.FlushAsync(serverCts.Token);
        await Task.Delay(Timeout.Infinite, serverCts.Token);
    });

    try
    {
        using var tuner = new ChannelTuner(vlc) { MaxAttempts = 1 };
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tuner.StateChanged += state => { if (state.Status == TunerStatus.Playing) playing.TrySetResult(); };
        tuner.Play(new TuneRequest("stalled", "Stalled local WAV", $"http://127.0.0.1:{port}/stall.wav",
            "local stalled fixture", false, 200));
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1200); // The server keeps the response open but sends no more bytes.
        tuner.Stop();
        await WaitUntilAsync(() => tuner.CurrentPlayer is null, TimeSpan.FromSeconds(5));
        await Task.Run(() => tuner.Dispose()).WaitAsync(TimeSpan.FromSeconds(8));
        Console.WriteLine("PASS native stalled local response stops and retires without hanging");
    }
    finally
    {
        serverCts.Cancel();
        listener.Stop();
        try { await server; } catch (OperationCanceledException) { }
        catch (SocketException) { }
    }
}
