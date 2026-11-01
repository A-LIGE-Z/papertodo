using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaperTodo;

public sealed record SyncTestResult(bool Success, string Message, long? RemoteFileSize = null);

public interface ISyncStorageProvider : IDisposable
{
    Task<SyncTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<string?> DownloadDataJsonAsync(CancellationToken cancellationToken = default);
    Task<bool> UploadDataJsonAsync(string json, CancellationToken cancellationToken = default);
}

public static class SyncProviderFactory
{
    public static ISyncStorageProvider Create(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var provider = SyncProviders.Normalize(state.SyncProvider);
        return provider switch
        {
            SyncProviders.S3 => new S3SyncProvider(
                state.SyncS3Endpoint,
                state.SyncS3Bucket,
                state.SyncS3AccessKey,
                state.SyncS3SecretKey,
                state.SyncS3Region),
            SyncProviders.Custom => new CustomHttpSyncProvider(
                state.SyncCustomServerUrl,
                state.SyncCustomToken),
            _ => new WebDavSyncProvider(
                state.SyncWebDavUrl,
                state.SyncWebDavUsername,
                state.SyncWebDavPassword,
                state.SyncWebDavRemotePath)
        };
    }
}

public sealed class WebDavSyncProvider : ISyncStorageProvider
{
    public void Dispose() => _client.Dispose();
    private readonly SyncRevision _revision = new();
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly string _remotePath;

    public WebDavSyncProvider(string url, string username, string password, string remotePath)
    {
        _baseUrl = (url ?? "").Trim().TrimEnd('/');
        _remotePath = (string.IsNullOrWhiteSpace(remotePath) ? "/papertodo/data.json" : remotePath.Trim());
        if (!_remotePath.StartsWith('/'))
        {
            _remotePath = "/" + _remotePath;
        }

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        if (!string.IsNullOrEmpty(username))
        {
            var authBytes = Encoding.UTF8.GetBytes($"{username}:{password ?? ""}");
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }
        _client.DefaultRequestHeaders.Add("User-Agent", "PaperTodo-Sync/0.0.3");
    }

    private Uri GetTargetUri(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            throw new InvalidOperationException(Strings.Get("SyncUrlEmpty"));
        }
        var cleanRel = relativePath.TrimStart('/');
        return new Uri($"{_baseUrl}/{cleanRel}");
    }

    public async Task<SyncTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            return new SyncTestResult(false, Strings.Get("SyncUrlEmpty"));
        }

        try
        {
            // Test root or file with PROPFIND or HEAD
            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), GetTargetUri(""))
            {
                Headers = { { "Depth", "0" } }
            };

            var response = await _client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MultiStatus)
            {
                return new SyncTestResult(true, Strings.Get("SyncConnectSuccess"));
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new SyncTestResult(false, Strings.Get("SyncAuthFailed"));
            }

            // Fallback: try GET on remote file or base
            var getReq = new HttpRequestMessage(HttpMethod.Get, GetTargetUri(_remotePath));
            var getResp = await _client.SendAsync(getReq, cancellationToken);
            if (getResp.IsSuccessStatusCode)
            {
                var len = getResp.Content.Headers.ContentLength;
                return new SyncTestResult(true, Strings.Get("SyncConnectSuccess"), len);
            }
            if (getResp.StatusCode == HttpStatusCode.NotFound)
            {
                return new SyncTestResult(true, Strings.Get("SyncConnectSuccessNewFile"));
            }

            return new SyncTestResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new SyncTestResult(false, ex.Message);
        }
    }

    public async Task<string?> DownloadDataJsonAsync(CancellationToken cancellationToken = default)
    {
        var targetUri = GetTargetUri(_remotePath);
        using var response = await _client.GetAsync(targetUri, cancellationToken);
        _revision.Observe(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<bool> UploadDataJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var targetUri = GetTargetUri(_remotePath);

        // Ensure parent folder exists
        await EnsureParentDirectoryExistsAsync(_remotePath, cancellationToken);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, targetUri) { Content = content };
        _revision.Apply(request);
        using var response = await _client.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Retry after creating folder
            await EnsureParentDirectoryExistsAsync(_remotePath, cancellationToken);
            using var retryContent = new StringContent(json, Encoding.UTF8, "application/json");
            using var retry = new HttpRequestMessage(HttpMethod.Put, targetUri) { Content = retryContent };
            _revision.Apply(retry);
            using var retryResp = await _client.SendAsync(retry, cancellationToken);
            SyncRevision.Check(retryResp);
            _revision.Observe(retryResp);
            return true;
        }

        SyncRevision.Check(response);
        _revision.Observe(response);
        return true;
    }

    private async Task EnsureParentDirectoryExistsAsync(string path, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path.Replace('\\', '/'))?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(parent) || parent == "/")
        {
            return;
        }

        var segments = parent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var seg in segments)
        {
            current += "/" + seg;
            try
            {
                var mkcolReq = new HttpRequestMessage(new HttpMethod("MKCOL"), GetTargetUri(current));
                using var resp = await _client.SendAsync(mkcolReq, cancellationToken);
            }
            catch
            {
                // Directory may already exist
            }
        }
    }
}

public sealed class S3SyncProvider : ISyncStorageProvider
{
    public void Dispose() => _client.Dispose();
    private readonly SyncRevision _revision = new();
    private readonly string _endpoint;
    private readonly string _bucket;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _region;
    private readonly HttpClient _client;
    private const string ObjectKey = "papertodo/data.json";

    public S3SyncProvider(string endpoint, string bucket, string accessKey, string secretKey, string region)
    {
        _endpoint = (endpoint ?? "").Trim().TrimEnd('/');
        _bucket = (bucket ?? "").Trim();
        _accessKey = (accessKey ?? "").Trim();
        _secretKey = secretKey ?? "";
        _region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region.Trim();

        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _client.DefaultRequestHeaders.Add("User-Agent", "PaperTodo-Sync/0.0.3");
    }

    private Uri GetObjectUri()
    {
        if (string.IsNullOrWhiteSpace(_endpoint) || string.IsNullOrWhiteSpace(_bucket))
        {
            throw new InvalidOperationException("S3 endpoint or bucket is empty.");
        }

        // Support path-style S3 endpoint: https://endpoint/bucket/key
        return new Uri($"{_endpoint}/{_bucket}/{ObjectKey}");
    }

    public async Task<SyncTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint) || string.IsNullOrWhiteSpace(_bucket))
        {
            return new SyncTestResult(false, Strings.Get("SyncS3ConfigIncomplete"));
        }

        try
        {
            var uri = GetObjectUri();
            var req = new HttpRequestMessage(HttpMethod.Head, uri);
            SignS3Request(req, uri, "HEAD", Array.Empty<byte>());

            using var resp = await _client.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                return new SyncTestResult(true, Strings.Get("SyncConnectSuccess"), resp.Content.Headers.ContentLength);
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return new SyncTestResult(true, Strings.Get("SyncConnectSuccessNewFile"));
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new SyncTestResult(false, Strings.Get("SyncAuthFailed"));
            }
            return new SyncTestResult(false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new SyncTestResult(false, ex.Message);
        }
    }

    public async Task<string?> DownloadDataJsonAsync(CancellationToken cancellationToken = default)
    {
        var uri = GetObjectUri();
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        SignS3Request(req, uri, "GET", Array.Empty<byte>());

        using var resp = await _client.SendAsync(req, cancellationToken);
        _revision.Observe(resp);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<bool> UploadDataJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var uri = GetObjectUri();
        var payloadBytes = Encoding.UTF8.GetBytes(json);

        var req = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(payloadBytes)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        _revision.Apply(req);
        SignS3Request(req, uri, "PUT", payloadBytes);

        using var resp = await _client.SendAsync(req, cancellationToken);
        SyncRevision.Check(resp);
        _revision.Observe(resp);
        return true;
    }

    private void SignS3Request(HttpRequestMessage req, Uri uri, string httpMethod, byte[] payload)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = now.ToString("yyyyMMdd");

        req.Headers.Add("x-amz-date", amzDate);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        req.Headers.Add("x-amz-content-sha256", payloadHash);

        var host = uri.Host + (uri.IsDefaultPort ? "" : $":{uri.Port}");
        req.Headers.Host = host;

        var canonicalUri = uri.AbsolutePath;
        var canonicalQuery = "";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = $"{httpMethod}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var canonicalRequestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        var credentialScope = $"{dateStamp}/{_region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

        var signingKey = GetSignatureKey(_secretKey, dateStamp, _region, "s3");
        var signature = Convert.ToHexStringLower(HmacSha256(signingKey, stringToSign));

        var authHeader = $"AWS4-HMAC-SHA256 Credential={_accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + key);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, regionName);
        var kService = HmacSha256(kRegion, serviceName);
        return HmacSha256(kService, "aws4_request");
    }
}

public sealed class CustomHttpSyncProvider : ISyncStorageProvider
{
    public void Dispose() => _client.Dispose();
    private readonly SyncRevision _revision = new();
    private readonly string _serverUrl;
    private readonly string _token;
    private readonly HttpClient _client;

    public CustomHttpSyncProvider(string serverUrl, string token)
        : this(serverUrl, token, new HttpClientHandler()) { }

    public CustomHttpSyncProvider(string serverUrl, string token, HttpMessageHandler handler)
    {
        _serverUrl = (serverUrl ?? "").Trim().TrimEnd('/');
        _token = (token ?? "").Trim();
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        if (!string.IsNullOrEmpty(_token))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        _client.DefaultRequestHeaders.Add("User-Agent", "PaperTodo-Sync/0.0.3");
    }

    private Uri GetApiUri(string path)
    {
        if (string.IsNullOrWhiteSpace(_serverUrl))
        {
            throw new InvalidOperationException("Server URL is empty.");
        }
        return new Uri($"{_serverUrl}/{path.TrimStart('/')}");
    }

    public async Task<SyncTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_serverUrl))
        {
            return new SyncTestResult(false, Strings.Get("SyncUrlEmpty"));
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GetApiUri("/api/v1/sync"));
            using var resp = await _client.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                SyncProtocol.Deserialize(await resp.Content.ReadAsStringAsync(cancellationToken));
                return new SyncTestResult(true, Strings.Get("SyncConnectSuccess"));
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return new SyncTestResult(false, Strings.Get("SyncAuthFailed"));
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                using var ping = await _client.GetAsync(GetApiUri("/api/v1/ping"), cancellationToken);
                if (ping.IsSuccessStatusCode)
                {
                    using var body = System.Text.Json.JsonDocument.Parse(await ping.Content.ReadAsStringAsync(cancellationToken));
                    if (body.RootElement.TryGetProperty("app", out var app) && app.GetString() == "PaperTodo.SyncServer")
                        return new SyncTestResult(true, Strings.Get("SyncConnectSuccessNewFile"));
                }
                return new SyncTestResult(false, Strings.Get("SyncInvalidEndpoint"));
            }
            return new SyncTestResult(false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (System.Text.Json.JsonException)
        {
            return new SyncTestResult(false, Strings.Get("SyncInvalidEndpoint"));
        }
        catch (Exception ex)
        {
            return new SyncTestResult(false, ex.Message);
        }
    }

    public async Task<string?> DownloadDataJsonAsync(CancellationToken cancellationToken = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, GetApiUri("/api/v1/sync"));
        using var resp = await _client.SendAsync(req, cancellationToken);
        _revision.Observe(resp);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<bool> UploadDataJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, GetApiUri("/api/v1/sync"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        _revision.Apply(req);
        using var resp = await _client.SendAsync(req, cancellationToken);
        SyncRevision.Check(resp);
        _revision.Observe(resp);
        return true;
    }
}
