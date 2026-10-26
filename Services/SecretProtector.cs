using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KiteShell.Services;

/// <summary>
/// 使用 AES-GCM 加密本地保存的密码。密钥存放在用户配置目录并限制为仅当前用户可读，
/// 属于本机范围内的静态加密保护，不替代系统级密钥环。
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(string keyFile)
    {
        AppPaths.EnsureDataDirectory();
        _key = LoadOrCreateKey(keyFile);
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, payload, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return null;
        }

        try
        {
            var payload = Convert.FromBase64String(protectedText);
            if (payload.Length < NonceSize + TagSize)
            {
                return null;
            }

            var nonce = payload.AsSpan(0, NonceSize);
            var tag = payload.AsSpan(NonceSize, TagSize);
            var cipher = payload.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] LoadOrCreateKey(string keyFile)
    {
        if (File.Exists(keyFile))
        {
            try
            {
                var existing = File.ReadAllBytes(keyFile);
                if (existing.Length >= 32)
                {
                    return existing.AsSpan(0, 32).ToArray();
                }
            }
            catch
            {
                // 损坏时重新生成密钥（旧密码将无法解密，用户会被要求重新输入）。
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var temp = keyFile + ".tmp";
        File.WriteAllBytes(temp, key);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // 忽略不支持的文件系统。
            }
        }

        File.Move(temp, keyFile, overwrite: true);
        return key;
    }
}
