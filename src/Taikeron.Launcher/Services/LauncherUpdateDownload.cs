using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace Taikeron.Launcher.Services;

public sealed record LauncherDownloadProgress(
    string Label, long Received, long Total, double BytesPerSecond, int Attempt, int MaxAttempts, string? Notice = null);

public static class LauncherUpdateDownload
{
    public static async Task DownloadAsync(HttpClient client, string url, string destination,
        long expectedBytes, string label, IProgress<LauncherDownloadProgress>? progress,
        CancellationToken cancellationToken, TimeSpan? idleTimeout = null, TimeSpan? attemptTimeout = null,
        TimeSpan? retryDelay = null)
    {
        if (string.IsNullOrWhiteSpace(url) || expectedBytes <= 0)
            throw new InvalidDataException("URL ou taille officielle du téléchargement Launcher invalide.");

        const int maxAttempts = 3;
        var idle = idleTimeout ?? TimeSpan.FromSeconds(45);
        var maximum = attemptTimeout ?? TimeSpan.FromMinutes(15);
        var delay = retryDelay ?? TimeSpan.FromSeconds(3);
        var partial = destination + ".part";
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(new(label, 0, expectedBytes, 0, attempt, maxAttempts, "Connexion à GitHub…"));
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(maximum);
                var token = attemptCts.Token;
                using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                headersCts.CancelAfter(idle);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersCts.Token);
                response.EnsureSuccessStatusCode();

                long received = 0;
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readCts.CancelAfter(idle);
                await using var source = await response.Content.ReadAsStreamAsync(readCts.Token);
                await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write,
                    FileShare.Read, 128 * 1024, useAsync: true))
                {
                    var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        // Restart the inactivity deadline only after each successful read/write.
                        readCts.CancelAfter(idle);
                        var count = await source.ReadAsync(buffer.AsMemory(), readCts.Token);
                        if (count == 0)
                            break;
                        received += count;
                        if (received > expectedBytes)
                            throw new InvalidDataException("Le téléchargement dépasse la taille officielle.");
                        await target.WriteAsync(buffer.AsMemory(0, count), readCts.Token);
                        if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250) || received == expectedBytes)
                        {
                            progress?.Report(new(label, received, expectedBytes,
                                received / Math.Max(clock.Elapsed.TotalSeconds, 0.001), attempt, maxAttempts));
                            lastReport = clock.Elapsed;
                        }
                    }
                    readCts.CancelAfter(idle);
                    await target.FlushAsync(readCts.Token);
                }

                if (received != expectedBytes || new FileInfo(partial).Length != expectedBytes)
                    throw new InvalidDataException("Taille du téléchargement Launcher différente de la taille officielle.");

                token.ThrowIfCancellationRequested();
                File.Move(partial, destination, overwrite: true);
                progress?.Report(new(label, received, expectedBytes,
                    received / Math.Max(clock.Elapsed.TotalSeconds, 0.001), attempt, maxAttempts));
                return;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("Délai de téléchargement dépassé (connexion inactive ou durée maximale).", ex);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null ||
                ex.StatusCode == HttpStatusCode.RequestTimeout || (int)ex.StatusCode.Value == 429 ||
                (int)ex.StatusCode.Value >= 500)
            {
                lastError = ex;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
            {
                lastError = ex;
            }
            finally
            {
                // Never leave a partial file eligible for installation.
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            if (attempt < maxAttempts)
            {
                progress?.Report(new(label, 0, expectedBytes, 0, attempt, maxAttempts,
                    "Transfert interrompu. Nouvelle tentative…"));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new IOException($"Téléchargement Launcher impossible après {maxAttempts} tentatives : {lastError?.Message}", lastError);
    }
}
