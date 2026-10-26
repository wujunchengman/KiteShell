using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using KiteShell.Models;

namespace KiteShell.Services;

/// <summary>
/// System.Text.Json 源生成上下文，避免 NativeAOT 下的反射序列化。
/// 选项显式保持与旧版反射序列化一致（camelCase 属性名、camelCase 枚举值、缩进输出），
/// 以确保已存在的 sessions.json / snippets.json / known_hosts.json 仍可读写。
/// </summary>
[JsonSerializable(typeof(SessionFile))]
[JsonSerializable(typeof(List<Snippet>))]
[JsonSerializable(typeof(Dictionary<string, KnownHostEntry>))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter<SessionNodeType>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<SshAuthMethod>(JsonNamingPolicy.CamelCase),
        },
    };

    /// <summary>使用兼容选项的共享上下文实例。</summary>
    public static AppJsonContext Compatible { get; } = new(SerializerOptions);
}

/// <summary>
/// 类型安全的上下文查询入口，避免依赖源生成器产生的属性命名规则。
/// </summary>
internal static class AppJson
{
    public static JsonTypeInfo<T> TypeInfo<T>()
    {
        var typeInfo = AppJsonContext.Compatible.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"类型 {typeof(T)} 未在 AppJsonContext 中注册。");
        return (JsonTypeInfo<T>)typeInfo;
    }
}
