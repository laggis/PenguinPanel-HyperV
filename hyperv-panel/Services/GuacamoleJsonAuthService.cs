using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Json;

namespace HyperVPanel.Services;

public sealed class GuacamoleJsonAuthService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, CachedConsoleUrl> _consoleUrlCache = new(StringComparer.Ordinal);

    public GuacamoleJsonAuthService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public bool IsEnabled => ReadBool("WebRdp:JsonAuth:Enabled", false) && !string.IsNullOrWhiteSpace(SecretKeyHex);

    public string SecretKeyHex => _configuration["WebRdp:JsonAuth:SecretKey"] ?? string.Empty;

    public string BuildConsoleUrl(string baseUrl, string vmName, string panelUsername, VmAccessInfo access)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var cacheKey = BuildCacheKey(baseUrl, vmName, panelUsername, access);
        if (_consoleUrlCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Url;
        }

        var url = BuildConsoleUrlUncached(baseUrl, vmName, panelUsername, access);
        if (!string.IsNullOrWhiteSpace(url))
        {
            var cacheSeconds = Math.Clamp(ReadInt("WebRdp:JsonAuth:CacheSeconds", 120), 15, 600);
            _consoleUrlCache[cacheKey] = new CachedConsoleUrl(url, DateTime.UtcNow.AddSeconds(cacheSeconds));
        }

        return url;
    }

    private string BuildConsoleUrlUncached(string baseUrl, string vmName, string panelUsername, VmAccessInfo access)
    {
        var host = !string.IsNullOrWhiteSpace(access.IpAddress) ? access.IpAddress : access.Hostname;
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var port = access.RdpPort is > 0 ? access.RdpPort.Value.ToString() : "3389";

        // Keep the JSON auth connection key stable. Some Guacamole builds are picky about
        // route/query parsing when the connection name changes per VM. The VM name still
        // appears in the temporary Guacamole username, but the route always points to the
        // same dynamic JSON connection id. This avoids intermittent "connection does not exist"
        // errors for one VM while another VM works.
        const string connectionName = "penguin-console";

        var username = string.IsNullOrWhiteSpace(panelUsername) ? "penguinpanel" : panelUsername;
        var expires = DateTimeOffset.UtcNow.AddMinutes(TokenLifetimeMinutes()).ToUnixTimeMilliseconds();

        var payload = new GuacamoleJsonAuthPayload
        {
            Username = SanitizeUsername($"pp-{username}-{vmName}"),
            Expires = expires,
            Connections = new Dictionary<string, GuacamoleJsonConnection>
            {
                [connectionName] = new()
                {
                    Id = connectionName,
                    Protocol = "rdp",
                    Parameters = BuildRdpParameters(host, port, access)
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encryptedData = EncryptAndSign(json, SecretKeyHex.Trim());
        if (string.IsNullOrWhiteSpace(encryptedData))
        {
            return string.Empty;
        }

        var clientIdentifier = BuildClientIdentifier(connectionName);
        var guacBase = NormalizeGuacamoleBaseUrl(baseUrl);
        var encodedData = Uri.EscapeDataString(encryptedData);

        if (string.IsNullOrWhiteSpace(guacBase))
        {
            return string.Empty;
        }

        // More reliable mode: POST the encrypted JSON to Guacamole /api/tokens first.
        // This avoids every browser/proxy/hash-route edge case around ?data= and #/client/.
        // Guacamole returns a normal authToken which can be used in the hash route.
        if (UseApiTokenMode())
        {
            var authToken = RequestGuacamoleAuthToken(guacBase, encryptedData);
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                return $"{guacBase}/#/client/{clientIdentifier}?token={Uri.EscapeDataString(authToken)}";
            }
        }

        // Fallback mode. The JSON auth payload must be a normal URL query parameter before the # route.
        return $"{guacBase}/?data={encodedData}#/client/{clientIdentifier}";
    }

    private static string NormalizeGuacamoleBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var trimmed = baseUrl.Trim();

        // Admins sometimes paste a full copied Guacamole connection URL here. Keep only the public
        // Guacamole base URL and remove any existing #/client route or ?data token.
        var hashIndex = trimmed.IndexOf('#');
        if (hashIndex >= 0)
        {
            trimmed = trimmed[..hashIndex];
        }

        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex >= 0)
        {
            trimmed = trimmed[..queryIndex];
        }

        return trimmed.TrimEnd('/');
    }

    public string BuildClientIdentifier(string connectionName)
    {
        var raw = $"{connectionName}\0c\0json";
        return ToBase64Url(Encoding.UTF8.GetBytes(raw));
    }

    private Dictionary<string, string> BuildRdpParameters(string host, string port, VmAccessInfo access)
    {
        var security = (_configuration["WebRdp:JsonAuth:Security"] ?? _configuration["WebRdp:Security"] ?? "nla").Trim().ToLowerInvariant();
        if (security == "nla" || security == "network-level-authentication")
        {
            security = "nla";
        }

        var parameters = new Dictionary<string, string>
        {
            ["hostname"] = host,
            ["port"] = port,
            ["security"] = security,
            ["ignore-cert"] = ReadBool("WebRdp:JsonAuth:IgnoreCertificate", true) ? "true" : "false",
            ["resize-method"] = _configuration["WebRdp:JsonAuth:ResizeMethod"] ?? "display-update"
        };

        if (!string.IsNullOrWhiteSpace(access.Username))
        {
            parameters["username"] = access.Username;
        }

        if (!string.IsNullOrWhiteSpace(access.Password))
        {
            parameters["password"] = access.Password;
        }

        if (!string.IsNullOrWhiteSpace(_configuration["WebRdp:JsonAuth:Domain"]))
        {
            parameters["domain"] = _configuration["WebRdp:JsonAuth:Domain"]!;
        }

        return parameters;
    }

    private int TokenLifetimeMinutes()
    {
        return int.TryParse(_configuration["WebRdp:JsonAuth:TokenLifetimeMinutes"], out var minutes) && minutes > 0
            ? minutes
            : 60;
    }

    private bool ReadBool(string key, bool defaultValue)
    {
        return bool.TryParse(_configuration[key], out var value) ? value : defaultValue;
    }

    private static string EncryptAndSign(string json, string secretKeyHex)
    {
        byte[] key;
        try
        {
            key = Convert.FromHexString(secretKeyHex);
        }
        catch
        {
            return string.Empty;
        }

        if (key.Length != 16)
        {
            return string.Empty;
        }

        var plainJson = Encoding.UTF8.GetBytes(json);
        using var hmac = new HMACSHA256(key);
        var signature = hmac.ComputeHash(plainJson);
        var signedPayload = new byte[signature.Length + plainJson.Length];
        Buffer.BlockCopy(signature, 0, signedPayload, 0, signature.Length);
        Buffer.BlockCopy(plainJson, 0, signedPayload, signature.Length, plainJson.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(signedPayload, 0, signedPayload.Length);
        return Convert.ToBase64String(encrypted);
    }

    private bool UseApiTokenMode()
    {
        // Enabled by default because it is more reliable behind reverse proxies.
        return ReadBool("WebRdp:JsonAuth:UseApiToken", true);
    }

    private string BuildCacheKey(string baseUrl, string vmName, string panelUsername, VmAccessInfo access)
    {
        var material = string.Join("\n",
            baseUrl?.Trim() ?? string.Empty,
            vmName?.Trim() ?? string.Empty,
            panelUsername?.Trim() ?? string.Empty,
            access.Hostname ?? string.Empty,
            access.IpAddress ?? string.Empty,
            access.RdpPort?.ToString() ?? string.Empty,
            access.Username ?? string.Empty,
            access.Password ?? string.Empty,
            WebRdpSecurityForCache(),
            SecretKeyHex.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private string WebRdpSecurityForCache() =>
        (_configuration["WebRdp:JsonAuth:Security"] ?? _configuration["WebRdp:Security"] ?? "nla").Trim();

    private int ReadInt(string key, int defaultValue) =>
        int.TryParse(_configuration[key], out var value) ? value : defaultValue;

    private string RequestGuacamoleAuthToken(string guacBase, string encryptedData)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var timeoutSeconds = Math.Clamp(ReadInt("WebRdp:JsonAuth:RequestTimeoutSeconds", 3), 1, 10);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            var tokenUrl = guacBase.TrimEnd('/') + "/api/tokens";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["data"] = encryptedData
            });

            using var response = client.PostAsync(tokenUrl, content).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("authToken", out var tokenElement))
            {
                return tokenElement.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Fall back to direct data URL if the API token call fails.
        }

        return string.Empty;
    }

    private static string BuildConnectionName(string vmName)
    {
        var value = string.IsNullOrWhiteSpace(vmName) ? "PenguinPanel-VM" : vmName.Trim();
        return value.Length > 64 ? value[..64] : value;
    }

    private static string SanitizeUsername(string value)
    {
        var sanitized = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray());
        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record CachedConsoleUrl(string Url, DateTime ExpiresAtUtc);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private sealed class GuacamoleJsonAuthPayload
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("expires")]
        public long Expires { get; set; }

        [JsonPropertyName("connections")]
        public Dictionary<string, GuacamoleJsonConnection> Connections { get; set; } = new();
    }

    private sealed class GuacamoleJsonConnection
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "rdp";

        [JsonPropertyName("parameters")]
        public Dictionary<string, string> Parameters { get; set; } = new();
    }
}
