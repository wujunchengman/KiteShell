using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Renci.SshNet;
using Renci.SshNet.Common;
using KiteShell.Models;

namespace KiteShell.Services;

public static class SshConnectionFactory
{
    public static ConnectionInfo Build(ConnectionSettings settings, SshInteraction interaction)
    {
        var methods = new List<AuthenticationMethod>();

        if (settings.AuthMethod == SshAuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(settings.PrivateKeyPath))
        {
            var keyFile = LoadPrivateKey(settings, interaction);
            methods.Add(new PrivateKeyAuthenticationMethod(settings.Username, keyFile));
        }

        if (!string.IsNullOrEmpty(interaction.Secrets.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(settings.Username, interaction.Secrets.Password));
        }

        var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(settings.Username);
        keyboardInteractive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                string? answer;
                if (!prompt.IsEchoed && !string.IsNullOrEmpty(interaction.Secrets.Password) && LooksLikePassword(prompt.Request))
                {
                    answer = interaction.Secrets.Password;
                }
                else
                {
                    answer = interaction.Prompt?.Invoke(
                        new AuthenticationPromptInfo(e.Instruction, prompt.Request, prompt.IsEchoed));
                }

                prompt.Response = answer ?? string.Empty;
            }
        };
        methods.Add(keyboardInteractive);

        var connectionInfo = new ConnectionInfo(settings.Host, settings.Port, settings.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        return connectionInfo;
    }

    public static void AttachHostKeyVerification(BaseClient client, SshInteraction interaction)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var host = interaction.Profile.Host;
            var port = interaction.Profile.Port;
            var keyType = string.IsNullOrWhiteSpace(e.HostKeyName) ? "unknown" : e.HostKeyName;
            var fingerprint = BuildFingerprint(e);
            var store = interaction.KnownHosts;
            if (store is null)
            {
                e.CanTrust = true;
                return;
            }

            var status = store.Check(host, port, keyType, fingerprint);
            if (status == KnownHostStatus.Match)
            {
                e.CanTrust = true;
                return;
            }

            HostKeyAction action;
            try
            {
                action = interaction.VerifyHostKey?.Invoke(
                    new HostKeyPromptInfo(host, port, keyType, fingerprint, status == KnownHostStatus.Mismatch))
                    ?? HostKeyAction.TrustOnce;
            }
            catch
            {
                action = HostKeyAction.Reject;
            }

            if (action == HostKeyAction.TrustAndSave)
            {
                store.Trust(host, port, keyType, fingerprint);
            }

            e.CanTrust = action != HostKeyAction.Reject;
        };
    }

    private static string BuildFingerprint(HostKeyEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(e.FingerPrintSHA256))
            {
                return e.FingerPrintSHA256.StartsWith("SHA256:", StringComparison.Ordinal)
                    ? e.FingerPrintSHA256
                    : "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            }
        }
        catch
        {
            // 某些服务端密钥算法不提供 SHA256 指纹。
        }

        try
        {
            if (e.FingerPrint is { Length: > 0 } md5)
            {
                return "MD5:" + BitConverter.ToString(md5).Replace("-", ":").ToLowerInvariant();
            }
        }
        catch
        {
            // 忽略。
        }

        return "未知指纹";
    }

    private static PrivateKeyFile LoadPrivateKey(ConnectionSettings settings, SshInteraction interaction)
    {
        var path = ExpandHome(settings.PrivateKeyPath!);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"私钥文件不存在：{path}");
        }

        try
        {
            return new PrivateKeyFile(path);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            // 私钥已加密，询问口令后重试。
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法加载私钥：{ex.Message}", ex);
        }

        var passphrase = interaction.Secrets.Passphrase;
        if (string.IsNullOrEmpty(passphrase))
        {
            passphrase = interaction.Prompt?.Invoke(
                new AuthenticationPromptInfo(string.Empty, $"私钥 {Path.GetFileName(path)} 的口令：", false));
            interaction.Secrets = interaction.Secrets with { Passphrase = passphrase };
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("私钥已加密，需要输入私钥口令。");
        }

        try
        {
            return new PrivateKeyFile(path, passphrase);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"私钥口令错误或私钥格式不受支持：{ex.Message}", ex);
        }
    }

    private static bool LooksLikePassword(string prompt)
    {
        return prompt.Contains("password", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("密码", StringComparison.Ordinal)
               || prompt.Contains("口令", StringComparison.Ordinal);
    }

    public static string ExpandHome(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path == "~" ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }
}

public sealed record ConnectionSettings(
    string Host,
    int Port,
    string Username,
    SshAuthMethod AuthMethod,
    string? PrivateKeyPath,
    string? StartupCommand)
{
    public static ConnectionSettings FromProfile(SessionNode profile) => new(
        profile.Host.Trim(),
        profile.Port,
        profile.Username.Trim(),
        profile.Auth,
        profile.PrivateKeyPath,
        profile.StartupCommand);
}
