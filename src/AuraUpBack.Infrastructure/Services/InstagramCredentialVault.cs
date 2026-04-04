using System.Security.Cryptography;
using System.Text;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InstagramCredentialVault(IOptions<InstagramIntegrationOptions> options) : IInstagramCredentialVault
{
    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.CredentialEncryptionKey));

    public string Encrypt(string plainText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var payload = new byte[aes.IV.Length + cipherBytes.Length];

        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string encryptedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedText);

        var payload = Convert.FromBase64String(encryptedText);
        using var aes = Aes.Create();
        aes.Key = _key;

        var ivLength = aes.BlockSize / 8;
        var iv = payload[..ivLength];
        var cipherBytes = payload[ivLength..];
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
