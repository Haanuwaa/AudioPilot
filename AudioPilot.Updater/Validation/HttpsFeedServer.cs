using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AudioPilot.Updater.Validation;

/// <summary>Bounded, disposable loopback HTTPS feed with a temporary guest-scoped Windows TLS identity.</summary>
internal sealed class HttpsFeedServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(8);
    private readonly ConcurrentDictionary<long, Task> _workers = new();
    private readonly X509Certificate2 _certificate;
    private readonly Task _accept;
    private readonly string _repository, _package, _target;
    private readonly string _etag;
    private long _sequence;
    public HttpsTestFeed Feed { get; }
    public int MetadataRequests, PackageRequests, ResumedRequests;
    public volatile bool DropNextPackage;
    public TaskCompletionSource? HoldPackage { get; set; }
    public HttpsFeedServer(OwnedWorkspace workspace, string package)
    {
        MsiDeployment.RequireSandbox();
        _repository = workspace.Resolve("tuf-repository"); _package = Path.GetFullPath(package); _target = Path.GetFileName(package);
        using (var bytes = File.OpenRead(_package)) _etag = Convert.ToHexString(SHA256.HashData(bytes));
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityRequest = new CertificateRequest("CN=AudioPilot Sandbox HTTPS authority", authorityKey, HashAlgorithmName.SHA256);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12));
        string publicPath = workspace.Resolve("https-root.pem"); File.WriteAllText(publicPath, authority.ExportCertificatePem());
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leaf = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256);
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); leaf.CertificateExtensions.Add(names.Build());
        using var issued = leaf.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(8), RandomNumberGenerator.GetBytes(16));
        using (var ephemeral = issued.CopyWithPrivateKey(leafKey))
        {
            byte[] encoded = ephemeral.Export(X509ContentType.Pkcs12);
            try { _certificate = X509CertificateLoader.LoadPkcs12(encoded, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        _listener.Start(16);
        Feed = new($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/", publicPath, Convert.ToHexString(SHA256.HashData(authority.RawData)), _target);
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_connections.Wait(0)) { socket.Dispose(); continue; }
                long id = Interlocked.Increment(ref _sequence);
                var task = ServeOwnedAsync(socket);
                _workers[id] = task;
                _ = ObserveAsync(id, task);
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ObserveAsync(long id, Task task)
    { try { await task; } finally { _workers.TryRemove(id, out _); } }
    private async Task ServeOwnedAsync(TcpClient socket) { try { await ServeAsync(socket); } finally { _connections.Release(); } }
    private async Task ServeAsync(TcpClient socket)
    {
        using (socket)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                using var stream = new SslStream(socket.GetStream());
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
                byte[] buffer = new byte[8192]; int length = 0;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
                    if (read == 0) return;
                    length += read;
                    if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    if (length == buffer.Length) throw new InvalidDataException("Oversized HTTP header.");
                }
                string[] lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
                string[] first = lines[0].Split(' ');
                if (first.Length != 3 || first[0] != "GET") { await Header(stream, 400, 0, "", deadline.Token); return; }
                string route = first[1]; string? path = null;
                bool package = route == "/targets/" + _target || route == "/" + _etag.ToLowerInvariant() + "." + _target;
                bool drop = false;
                if (package) { path = _package; Interlocked.Increment(ref PackageRequests); drop = DropNextPackage; DropNextPackage = false; }
                else if (route.StartsWith("/metadata/", StringComparison.Ordinal))
                {
                    string name = route[10..];
                    if (name.Length is > 0 and < 180 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && !name.Contains("..", StringComparison.Ordinal))
                        path = Path.Combine(_repository, "metadata", name);
                    Interlocked.Increment(ref MetadataRequests);
                }
                if (path == null || !File.Exists(path)) { await Header(stream, 404, 0, "", deadline.Token); return; }
                OwnedWorkspace.CheckAncestors(path);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long total = input.Length, offset = 0;
                string? range = lines.FirstOrDefault(line => line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase));
                if (package && range != null && range.EndsWith('-') && long.TryParse(range[13..^1], out long requested) && requested > 0 && requested < total)
                { offset = requested; Interlocked.Increment(ref ResumedRequests); }
                await Header(stream, offset > 0 ? 206 : 200, total - offset,
                    $"ETag: \"{_etag}\"\r\n" + (offset > 0 ? $"Content-Range: bytes {offset}-{total - 1}/{total}\r\n" : ""), deadline.Token);
                input.Position = offset; byte[] chunk = new byte[65536]; long sent = 0;
                while (true)
                {
                    int read = await input.ReadAsync(chunk, deadline.Token); if (read == 0) break;
                    await stream.WriteAsync(chunk.AsMemory(0, read), deadline.Token); sent += read;
                    if (package)
                    {
                        if (HoldPackage is { } hold) await hold.Task.WaitAsync(deadline.Token);
                        if (drop && sent >= 262144) return;
                        await Task.Delay(2, deadline.Token);
                    }
                }
            }
            catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            { if (!_stop.IsCancellationRequested && error is AuthenticationException) Console.Error.WriteLine("HTTPS test server authentication: " + error); }
        }
    }
    private static async Task Header(Stream stream, int status, long length, string extra, CancellationToken token) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nConnection: close\r\nContent-Length: {length}\r\n{extra}\r\n"), token);
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _accept;
        await Task.WhenAll(_workers.Values);
        _certificate.Dispose(); _connections.Dispose(); _stop.Dispose();
    }
}
