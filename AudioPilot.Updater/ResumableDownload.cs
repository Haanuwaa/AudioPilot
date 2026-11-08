using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AudioPilot.Updater;

internal sealed record PackageIdentity(Uri Url, long Length, string Sha256)
{
    public void Validate()
    {
        if (!Url.IsAbsoluteUri || Url.Scheme != Uri.UriSchemeHttps || Url.UserInfo.Length != 0 || Length is <= 0 or > 1024L * 1024 * 1024 ||
            Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid package identity.");
    }
}

internal enum DownloadOutcome { Verified, Paused, Cancelled }
internal sealed record DownloadCheckpoint(PackageIdentity Package, string? ETag, bool Paused);

/// <summary>One streaming transfer per workspace. A pause retains bytes; cancel discards them.</summary>
internal sealed class ResumableDownload(HttpClient client, OwnedWorkspace workspace, Func<long, CancellationToken, Task>? transferCheckpoint = null,
    TimeProvider? timeProvider = null, TimeSpan? inactivityTimeout = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _inactivityTimeout = inactivityTimeout is { } requested &&
        (requested <= TimeSpan.Zero || requested.TotalMilliseconds > uint.MaxValue - 1)
        ? throw new ArgumentOutOfRangeException(nameof(inactivityTimeout)) : inactivityTimeout ?? TimeSpan.FromSeconds(30);

    public async Task<DownloadOutcome> RunAsync(PackageIdentity package, CancellationToken pauseToken,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        package.Validate();
        using var ownership = workspace.Lock();
        string partial = workspace.Resolve("package.partial");
        string checkpointPath = workspace.Resolve("download.json");
        File.Delete(workspace.Resolve("package.verified"));
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(pauseToken, cancellationToken);
        CancellationToken transferToken = transfer.Token;
        DownloadCheckpoint? checkpoint = null;
        try
        {
            if (File.Exists(checkpointPath))
                checkpoint = OwnedWorkspace.ReadJson<DownloadCheckpoint>(checkpointPath);
        }
        catch (System.Text.Json.JsonException) { }
        if (checkpoint?.Package != package || (File.Exists(partial) && new FileInfo(partial).Length > package.Length))
        {
            File.Delete(partial);
            checkpoint = null;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pauseToken.IsCancellationRequested)
            {
                OwnedWorkspace.WriteJson(checkpointPath, new DownloadCheckpoint(package, checkpoint?.ETag, true));
                return DownloadOutcome.Paused;
            }

            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset < package.Length)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, package.Url);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    if (checkpoint?.ETag != null) request.Headers.IfRange = new RangeConditionHeaderValue(checkpoint.ETag);
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transferToken).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("Package transfer left HTTPS.");
                if (response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("Encoded package transfers cannot be resumed safely.");
                bool append = response.StatusCode == HttpStatusCode.PartialContent;
                if (append)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.Unit != "bytes" || range.From != offset || range.To != package.Length - 1 || range.Length != package.Length)
                        throw new InvalidDataException("Server returned an inconsistent byte range.");
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Expected a complete package response.");
                    offset = 0;
                }
                if (response.Content.Headers.ContentLength is long length && length != package.Length - offset)
                    throw new InvalidDataException("Package response length does not match its identity.");
                string? tag = response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;
                if (append && checkpoint?.ETag is string prior && tag != prior)
                    throw new InvalidDataException("Package changed while resuming.");
                OwnedWorkspace.WriteJson(checkpointPath, new DownloadCheckpoint(package, tag, false));
                checkpoint = new DownloadCheckpoint(package, tag, false);
                using var stalled = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _time);
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(transferToken, stalled.Token);
                await using var input = await response.Content.ReadAsStreamAsync(transferToken).ConfigureAwait(false);
                await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[65536];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count;
                    stalled.CancelAfter(_inactivityTimeout);
                    try { count = await input.ReadAsync(buffer, reading.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stalled.IsCancellationRequested && !transferToken.IsCancellationRequested)
                    { throw new TimeoutException("The package download stopped receiving data; partial bytes were retained."); }
                    finally { stalled.CancelAfter(Timeout.InfiniteTimeSpan); }
                    if (count == 0) break;
                    if (offset + count > package.Length) throw new InvalidDataException("Package exceeds the declared length.");
                    await output.WriteAsync(buffer.AsMemory(0, count), transferToken).ConfigureAwait(false);
                    offset += count;
                    progress?.Report(offset);
                    if (transferCheckpoint != null) await transferCheckpoint(offset, transferToken).ConfigureAwait(false);
                }
                await output.FlushAsync(transferToken).ConfigureAwait(false);
                output.Flush(true);
                if (offset != package.Length) throw new EndOfStreamException("Package transfer was interrupted; partial bytes were retained.");
            }
            await VerifyAsync(partial, package.Length, package.Sha256, transferToken).ConfigureAwait(false);
            transferToken.ThrowIfCancellationRequested();
            File.Move(partial, workspace.Resolve("package.verified"), overwrite: true);
            File.Delete(checkpointPath);
            return DownloadOutcome.Verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            File.Delete(partial);
            File.Delete(checkpointPath);
            return DownloadOutcome.Cancelled;
        }
        catch (OperationCanceledException) when (pauseToken.IsCancellationRequested)
        {
            if (File.Exists(partial)) { using var durable = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.Read); durable.Flush(true); }
            OwnedWorkspace.WriteJson(checkpointPath, new DownloadCheckpoint(package, checkpoint?.ETag, true));
            return DownloadOutcome.Paused;
        }
        catch (InvalidDataException)
        {
            File.Delete(partial);
            File.Delete(checkpointPath);
            throw;
        }
    }

    public static async Task VerifyAsync(string path, long length, string sha256, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != length || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package verification failed.");
    }
}
