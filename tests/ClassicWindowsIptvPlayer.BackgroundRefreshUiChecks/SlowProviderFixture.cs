using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Text;

internal sealed class SlowProviderFixture : IDisposable
{
    internal sealed class ResponsePlan(string body, int status)
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Body { get; } = body;
        public int Status { get; } = status;
    }
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly Queue<ResponsePlan> plans = new();
    int requests;
    public int Requests => Volatile.Read(ref requests);
    public string Url { get; }
    public SlowProviderFixture()
    {
        listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/fixture.m3u";
        _ = Accept();
    }
    public ResponsePlan Enqueue(string body, int status = 200)
    {
        var plan = new ResponsePlan(body, status);
        lock (plans) plans.Enqueue(plan);
        return plan;
    }
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Serve(client);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
            Interlocked.Increment(ref requests);
            ResponsePlan? plan;
            lock (plans) plan = plans.TryDequeue(out var next) ? next : null;
            if (plan is null) plan = new("Unexpected request", 500);
            else { plan.Requested.TrySetResult(); await plan.Release.Task.WaitAsync(stop.Token); }
            var body = Encoding.UTF8.GetBytes(plan.Body);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {plan.Status} Fixture\r\nContent-Type: audio/x-mpegurl\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, stop.Token);
            await stream.WriteAsync(body, stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); stop.Dispose(); }
}
