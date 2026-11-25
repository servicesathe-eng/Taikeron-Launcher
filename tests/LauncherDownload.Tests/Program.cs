using System.Net;
using Taikeron.Launcher.Services;

var root = Path.Combine(Path.GetTempPath(), "LauncherDownloadTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var data = new byte[] { 1, 2, 3, 4 };
    var path = Path.Combine(root, "package.zip");
    var reports = new List<LauncherDownloadProgress>();
    var progress = new InlineProgress(reports);
    var handler = new FakeHandler(n => n == 1
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    using (var client = new HttpClient(handler))
        await LauncherUpdateDownload.DownloadAsync(client, "https://example.test/package", path, data.Length,
            "test", progress, CancellationToken.None, retryDelay: TimeSpan.Zero);
    Assert(handler.Calls == 2 && File.ReadAllBytes(path).SequenceEqual(data), "retry / valid download");
    Assert(reports.Any(p => p.Attempt == 2 && p.Received == data.Length && p.BytesPerSecond > 0), "progress");
    Assert(!File.Exists(path + ".part"), "partial removed");
    File.Delete(path);

    var stalled = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) });
    using (var client = new HttpClient(stalled))
        await Expect<IOException>(() => LauncherUpdateDownload.DownloadAsync(client, "https://example.test/package",
            path, 4, "test", null, CancellationToken.None, idleTimeout: TimeSpan.FromMilliseconds(80),
            attemptTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero));
    Assert(stalled.Calls == 3 && !File.Exists(path) && !File.Exists(path + ".part"), "stall bounded and cleaned");

    var invalid = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    using (var client = new HttpClient(invalid))
        await Expect<IOException>(() => LauncherUpdateDownload.DownloadAsync(client, "https://example.test/package",
            path, 5, "test", null, CancellationToken.None, retryDelay: TimeSpan.Zero));
    Assert(invalid.Calls == 3 && !File.Exists(path) && !File.Exists(path + ".part"), "size mismatch rejected");

    var permanent = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
    using (var client = new HttpClient(permanent))
        await Expect<HttpRequestException>(() => LauncherUpdateDownload.DownloadAsync(client, "https://example.test/package",
            path, 4, "test", null, CancellationToken.None, retryDelay: TimeSpan.Zero));
    Assert(permanent.Calls == 1, "permanent HTTP error not retried");

    var cancelled = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) });
    using (var client = new HttpClient(cancelled))
    using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
        await Expect<OperationCanceledException>(() => LauncherUpdateDownload.DownloadAsync(client, "https://example.test/package",
            path, 4, "test", null, cts.Token, retryDelay: TimeSpan.Zero));
    Assert(cancelled.Calls == 1 && !File.Exists(path) && !File.Exists(path + ".part"), "cancellation without retry");
    Console.WriteLine("Launcher download: all five scenarios passed.");
}
finally { Directory.Delete(root, true); }

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}
static async Task Expect<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
sealed class InlineProgress(List<LauncherDownloadProgress> reports) : IProgress<LauncherDownloadProgress>
{
    public void Report(LauncherDownloadProgress value) => reports.Add(value);
}
sealed class FakeHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(response(++Calls));
}
sealed class StalledStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
