using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>One upstream reader, one append-only TS recording, independent tailing readers.
/// Positions are milliseconds in the recorded PCR timeline, never segment numbers.</summary>
internal sealed class ContinuousTimeshiftSource : IAsyncDisposable
{
    private readonly string _root = Path.Combine(AppDataPaths.Temp, "timeshift-" + Guid.NewGuid().ToString("N"));
    private readonly MediaPlayer _writer;
    private readonly Media _media;
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly object _gate = new();
    private readonly List<(long Time, long Offset)> _index = [];
    private readonly List<Task> _clients = [];
    private readonly Dictionary<Guid, long> _recordingCursors = [];
    private Task? _scan, _accept;
    private long _duration, _oldestTime, _oldestOffset, _trimmedUntilOffset, _bytes, _playbackPosition = -1, _lastTrimCheckTime;
    private int _retentionSeconds;
    private volatile bool _ended;
    private string? _failure;
    private string _prefix = "";
    public string FilePath => Path.Combine(_root, "session.ts");
    public string? TerminalFailure => _failure;
    public string? TimeshiftFailure => _failure;

    public ContinuousTimeshiftSource(LibVLC vlc, string url, int bufferMs, int retentionSeconds)
    {
        Directory.CreateDirectory(_root);
        _retentionSeconds = NormalizeRetention(retentionSeconds);
        try
        {
            using var recording = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (!DeviceIoControl(recording.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException("The temporary storage does not support sparse files required for bounded timeshift retention.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        catch { try { Directory.Delete(_root, true); } catch { } throw; }
        _media = new Media(vlc, url, FromType.FromLocation);
        _media.AddOption(":network-caching=" + bufferMs);
        _media.AddOption(":http-reconnect");
        _media.AddOption(":sout=#std{access=file,mux=ts,dst='" + FilePath.Replace('\\', '/').Replace("'", "\\'") + "'}");
        _media.AddOption(":sout-all");
        _writer = new MediaPlayer(vlc);
        _writer.EndReached += (_, _) => _ended = true;
        _writer.EncounteredError += (_, _) => { _failure = "The live recording input stopped receiving data."; _ended = true; };
    }

    public async Task<Uri> StartPlaybackAsync(CancellationToken token)
    {
        _listener.Start();
        _prefix = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/" + Guid.NewGuid().ToString("N") + "/";
        _accept = AcceptAsync();
        if (!_writer.Play(_media)) throw new IOException("The live recording could not start.");
        _scan = ScanAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        while (true)
        {
            lock (_gate) if (_index.Count > 0 && _duration >= 500) return new Uri(_prefix + "0.ts");
            if (_ended) throw new IOException(_failure ?? "The input ended before a TS timeline was available.");
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    public LiveTimeshiftWindow InspectTimeshiftWindow()
    {
        lock (_gate) return new(_index.Count > 0, _oldestTime, _duration,
            TimeSpan.FromMilliseconds(Math.Max(0, _duration - _oldestTime)), _bytes, long.MaxValue, null);
    }

    public void SetRetentionSeconds(int seconds)
    {
        lock (_gate) _retentionSeconds = NormalizeRetention(seconds);
    }

    public void UpdatePlaybackPosition(long absolutePositionMs, bool paused)
    {
        lock (_gate) _playbackPosition = Math.Max(0, absolutePositionMs);
    }

    public LiveTimeshiftSeekResult TryCreateTimeshiftPlaybackUrlBehindLive(TimeSpan behind, out Uri? url, out long position)
    {
        lock (_gate)
        {
            // Leave enough data for decoder probing and the next video keyframe.
            var target = Math.Max(_oldestTime, _duration - Math.Max(1500, (long)behind.TotalMilliseconds));
            var entry = _index.LastOrDefault(item => item.Time <= target);
            position = entry.Time;
            url = _index.Count == 0 ? null : new Uri(_prefix + entry.Offset + ".ts");
            return url is null ? LiveTimeshiftSeekResult.Empty : LiveTimeshiftSeekResult.Positioned;
        }
    }

    public LiveTimeshiftSeekResult TryCreateTimeshiftPlaybackUrl(long position, out Uri? url)
    {
        lock (_gate)
        {
            var entry = _index.LastOrDefault(item => item.Time <= Math.Max(position, _oldestTime));
            url = _index.Count == 0 ? null : new Uri(_prefix + entry.Offset + ".ts");
            return url is null ? LiveTimeshiftSeekResult.Empty : LiveTimeshiftSeekResult.Positioned;
        }
    }

    public bool TryLocatePlaybackPosition(long start, long elapsed, out long position, out long offset)
    {
        lock (_gate)
        {
            var target = Math.Clamp(start + Math.Max(0, elapsed), _oldestTime, _duration);
            var entry = _index.LastOrDefault(item => item.Time <= target);
            position = entry.Time;
            offset = target - position;
            return _index.Count > 0;
        }
    }

    public SharedHlsConsumer AttachRecording()
    {
        long offset;
        var cursorId = Guid.NewGuid();
        lock (_gate)
        {
            offset = _index.Count == 0 ? 0 : _index[^1].Offset;
            _recordingCursors[cursorId] = offset;
        }
        var detached = false;
        long sequence = 0;
        return new SharedHlsConsumer(async token =>
        {
            while (!Volatile.Read(ref detached) && !_stop.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                using var file = OpenFile();
                var count = (int)Math.Min(188 * 256, (file.Length - offset) / 188 * 188);
                if (count > 0)
                {
                    file.Position = offset;
                    var bytes = new byte[count];
                    file.ReadExactly(bytes);
                    offset += count;
                    lock (_gate) _recordingCursors[cursorId] = offset;
                    return new SharedHlsSegment(sequence++, 0, new Uri(FilePath), bytes, null);
                }
                if (_ended) return null;
                await Task.Delay(50, token).ConfigureAwait(false);
            }
            return null;
        }, () =>
        {
            Volatile.Write(ref detached, true);
            lock (_gate) _recordingCursors.Remove(cursorId);
        });
    }

    private async Task ScanAsync()
    {
        try
        {
            while (!File.Exists(FilePath)) await Task.Delay(25, _stop.Token).ConfigureAwait(false);
            // LibVLC creates/truncates the sout destination itself and may clear
            // the sparse attribute that was applied to the placeholder above.
            // Mark the real output sparse as soon as its first write appears.
            while (new FileInfo(FilePath).Length == 0) await Task.Delay(25, _stop.Token).ConfigureAwait(false);
            SetSparseStorage();
            using var file = OpenFile();
            var packet = new byte[188];
            long previousPcr = -1, ticks = 0, patOffset = 0, patTime = 0;
            int pcrPid = -1;
            while (!_stop.IsCancellationRequested)
            {
                if (file.Length - file.Position < 188)
                {
                    if (_ended) break;
                    await Task.Delay(25, _stop.Token).ConfigureAwait(false);
                    continue;
                }
                var packetOffset = file.Position;
                file.ReadExactly(packet);
                if (packet[0] != 0x47) throw new IOException("The recording is not a valid MPEG transport stream.");
                var pid = ((packet[1] & 31) << 8) | packet[2];
                if (pid == 0) { patOffset = packetOffset; patTime = ticks / 90; }
                if ((packet[3] & 32) == 0 || packet[4] < 7 || (packet[5] & 16) == 0) continue;
                if (pcrPid < 0) pcrPid = pid;
                if (pcrPid != pid) continue;
                long pcr = ((long)packet[6] << 25) | ((long)packet[7] << 17) | ((long)packet[8] << 9) | ((long)packet[9] << 1) | (uint)(packet[10] >> 7);
                if (previousPcr >= 0)
                {
                    var delta = (pcr - previousPcr + (1L << 33)) % (1L << 33);
                    // A source timestamp reset must not jump the recorded timeline by 26 hours.
                    if (delta < 90000 * 10) ticks += delta;
                }
                previousPcr = pcr;
                lock (_gate)
                {
                    _duration = ticks / 90;
                    _bytes = Math.Max(0, file.Length - _trimmedUntilOffset);
                    if (_index.Count == 0 || patTime - _index[^1].Time >= 200)
                        _index.Add((patTime, patOffset));
                    if (_duration - _lastTrimCheckTime >= 200)
                    {
                        _lastTrimCheckTime = _duration;
                        TrimOldHistory();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _failure = AppLogger.SanitizeText(ex.Message); _ended = true; }
    }

    private void SetSparseStorage()
    {
        using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (!DeviceIoControl(file.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("The temporary recording could not be marked sparse for bounded timeshift retention.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    private void TrimOldHistory()
    {
        if (_index.Count < 2) return;
        var cutoff = Math.Max(0, _duration - _retentionSeconds * 1000L);
        if (_playbackPosition >= 0) cutoff = Math.Min(cutoff, _playbackPosition);
        foreach (var cursor in _recordingCursors.Values)
        {
            var recorded = _index.LastOrDefault(item => item.Offset <= cursor);
            cutoff = Math.Min(cutoff, recorded.Time);
        }
        var keep = _index.LastOrDefault(item => item.Time <= cutoff);
        _oldestTime = keep.Time;
        _oldestOffset = keep.Offset;
        _index.RemoveAll(item => item.Offset < keep.Offset);
        // NTFS sparse extents are released at compression-unit boundaries.
        // Coalesce the small TS packets that age out between scans so each
        // zero request can actually release a complete storage unit.
        const long sparseUnit = 64 * 1024;
        var end = keep.Offset - keep.Offset % sparseUnit;
        if (end - _trimmedUntilOffset < sparseUnit)
        {
            _bytes = Math.Max(0, new FileInfo(FilePath).Length - _trimmedUntilOffset);
            return;
        }
        try
        {
            using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            file.Flush(flushToDisk: true);
            var zero = new FileZeroDataInformation { FileOffset = _trimmedUntilOffset, BeyondFinalZero = end };
            if (!DeviceIoControl(file.SafeFileHandle, FsctlSetZeroData, ref zero,
                    (uint)Marshal.SizeOf<FileZeroDataInformation>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                _failure = "Timeshift retention could not release expired temporary data.";
                _ended = true;
                AppLogger.Warn(_failure + " " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                return;
            }
            file.Flush(flushToDisk: true);
            _trimmedUntilOffset = end;
            _bytes = Math.Max(0, new FileInfo(FilePath).Length - _trimmedUntilOffset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _failure = "Timeshift retention could not release expired temporary data.";
            _ended = true;
            AppLogger.Warn(_failure + " " + AppLogger.SanitizeText(exception.Message));
        }
    }

    private static int NormalizeRetention(int seconds) => Math.Clamp(seconds, 1, 120);

    private const uint FsctlSetSparse = 0x000900C4;
    private const uint FsctlSetZeroData = 0x000980C8;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileZeroDataInformation
    {
        public long FileOffset;
        public long BeyondFinalZero;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        ref FileZeroDataInformation input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    private FileStream OpenFile() => new(FilePath, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 188 * 256, FileOptions.SequentialScan);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _clients.RemoveAll(task => task.IsCompleted);
                    _clients.Add(ServeAsync(client));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await reader.ReadLineAsync(requestTimeout.Token).ConfigureAwait(false);
            var path = request?.Split(' ').ElementAtOrDefault(1);
            var expectedPrefix = new Uri(_prefix).AbsolutePath;
            if (path is null || !path.StartsWith(expectedPrefix, StringComparison.Ordinal) || !path.EndsWith(".ts") ||
                !long.TryParse(path[expectedPrefix.Length..^3], out var offset) || offset < 0 || offset % 188 != 0) return;
            int headerBytes = 0;
            while (await reader.ReadLineAsync(requestTimeout.Token).ConfigureAwait(false) is { Length: > 0 } line)
                if ((headerBytes += line.Length) > 16384) return;
            using var file = OpenFile();
            long oldestOffset;
            lock (_gate) oldestOffset = _oldestOffset;
            if (offset < oldestOffset || offset > file.Length) return;
            file.Position = offset;
            // No finite length or EOF at the writer's current position. VLC can pause
            // this reader indefinitely while the independent writer keeps appending.
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), _stop.Token).ConfigureAwait(false);
            var buffer = new byte[188 * 256];
            while (!_stop.IsCancellationRequested)
            {
                var available = (int)Math.Min(buffer.Length, (file.Length - file.Position) / 188 * 188);
                if (available == 0)
                {
                    if (_ended) break;
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0) break;
                    await Task.Delay(25, _stop.Token).ConfigureAwait(false);
                    continue;
                }
                var count = await file.ReadAsync(buffer.AsMemory(0, available), _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(buffer.AsMemory(0, count), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await Task.Run(() => { _writer.Stop(); _writer.Dispose(); _media.Dispose(); }).ConfigureAwait(false);
        if (_scan is not null) await _scan.ConfigureAwait(false);
        if (_accept is not null) await _accept.ConfigureAwait(false);
        Task[] clients;
        lock (_gate) clients = [.. _clients];
        await Task.WhenAll(clients).ConfigureAwait(false);
        _stop.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
