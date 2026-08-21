using System.Security.Cryptography;
using System.Text;

namespace HyperVPanel.Services;

public sealed class SecretProtector(IConfiguration configuration)
{
    private const string Prefix = "enc:v1:";

    public string Protect(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return string.Empty;
        }

        if (plainText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plainText;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(GetKey(), 16);
        aes.Encrypt(nonce, bytes, cipher, tag);

        return Prefix + Convert.ToBase64String(Combine(nonce, tag, cipher));
    }

    public string Unprotect(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return string.Empty;
        }

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Backwards compatibility: old databases stored the password as plaintext.
            return protectedText;
        }

        try
        {
            var payload = Convert.FromBase64String(protectedText[Prefix.Length..]);
            if (payload.Length < 29)
            {
                return string.Empty;
            }

            var nonce = payload[..12];
            var tag = payload[12..28];
            var cipher = payload[28..];
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(GetKey(), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }

    private byte[] GetKey()
    {
        var key = configuration["Security:DataProtectionKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            key = Environment.GetEnvironmentVariable("PENGUINPANEL_DATA_PROTECTION_KEY");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.MachineName + ":" + AppContext.BaseDirectory;
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    private static byte[] Combine(params byte[][] chunks)
    {
        var length = chunks.Sum(item => item.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }
}
