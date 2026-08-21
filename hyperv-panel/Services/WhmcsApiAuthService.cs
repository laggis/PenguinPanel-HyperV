using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace HyperVPanel.Services;

public sealed class WhmcsApiAuthService(IConfiguration configuration)
{
    private readonly ConcurrentDictionary<string, RateBucket> _rateBuckets = new(StringComparer.OrdinalIgnoreCase);

    public WhmcsAuthDecision Authorize(HttpContext context)
    {
        if (!configuration.GetValue("WhmcsApi:Enabled", false))
        {
            return new(false, StatusCodes.Status503ServiceUnavailable, "WHMCS API is disabled.");
        }

        if (configuration.GetValue("WhmcsApi:RequireHttps", true) && !IsHttps(context))
        {
            return new(false, StatusCodes.Status403Forbidden, "HTTPS is required for the WHMCS API.");
        }

        var remoteIp = ResolveRemoteIp(context);
        var allowedIps = configuration.GetSection("WhmcsApi:AllowedIps").Get<string[]>() ?? [];
        if (allowedIps.Length > 0 && !allowedIps.Any(item => IpMatches(item, remoteIp)))
        {
            return new(false, StatusCodes.Status403Forbidden, "This IP address is not allowed to use the WHMCS API.");
        }

        var suppliedKey = ReadApiKey(context);
        if (string.IsNullOrWhiteSpace(suppliedKey))
        {
            return new(false, StatusCodes.Status401Unauthorized, "Missing WHMCS API key.");
        }

        var expectedHash = (configuration["WhmcsApi:ApiKeySha256"] ?? string.Empty).Trim();
        if (expectedHash.Length != 64 || !TryHex(expectedHash, out var expectedBytes))
        {
            return new(false, StatusCodes.Status503ServiceUnavailable, "WHMCS API key hash is not configured correctly.");
        }

        var actualBytes = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        if (!CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
        {
            return new(false, StatusCodes.Status401Unauthorized, "Invalid WHMCS API key.");
        }

        var maxPerMinute = Math.Clamp(configuration.GetValue("WhmcsApi:MaxRequestsPerMinute", 120), 10, 5000);
        var bucketKey = string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp;
        if (IsRateLimited(bucketKey, maxPerMinute))
        {
            return new(false, StatusCodes.Status429TooManyRequests, "WHMCS API rate limit exceeded. Try again shortly.");
        }

        return new(true, StatusCodes.Status200OK, string.Empty);
    }

    private bool IsRateLimited(string key, int maxPerMinute)
    {
        var now = DateTime.UtcNow;
        var bucket = _rateBuckets.GetOrAdd(key, _ => new RateBucket(now));
        lock (bucket)
        {
            if ((now - bucket.WindowStartedAt) >= TimeSpan.FromMinutes(1))
            {
                bucket.WindowStartedAt = now;
                bucket.Count = 0;
            }

            bucket.Count++;
            return bucket.Count > maxPerMinute;
        }
    }

    private string ResolveRemoteIp(HttpContext context)
    {
        if (configuration.GetValue("WhmcsApi:TrustForwardedFor", false)
            && context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var first = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    private static bool IsHttps(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            return true;
        }

        return context.Request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto)
            && string.Equals(forwardedProto.ToString().Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IpMatches(string configured, string remoteIp)
    {
        configured = (configured ?? string.Empty).Trim();
        remoteIp = (remoteIp ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(remoteIp))
        {
            return false;
        }

        if (IPAddress.TryParse(configured, out var configuredIp) && IPAddress.TryParse(remoteIp, out var remote))
        {
            return configuredIp.Equals(remote);
        }

        return string.Equals(configured, remoteIp, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadApiKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Penguin-WHMCS-Key", out var direct))
        {
            return direct.ToString().Trim();
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString().Trim();
            const string prefix = "Bearer ";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    private static bool TryHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == 32;
        }
        catch
        {
            bytes = [];
            return false;
        }
    }

    private sealed class RateBucket(DateTime startedAt)
    {
        public DateTime WindowStartedAt { get; set; } = startedAt;
        public int Count { get; set; }
    }
}

public sealed record WhmcsAuthDecision(bool Success, int StatusCode, string Error);
