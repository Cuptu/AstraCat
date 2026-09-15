using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Runtime.CompilerServices;

namespace AstraCat;

/// <summary>仅使用生成的 JSON 元数据；未知类型直接失败，不回退反射。保持原有 JSON 字段和选项。</summary>
internal static class AotJson
{
    private static readonly IJsonTypeInfoResolver Resolver = JsonTypeInfoResolver.Combine(
        AppJsonContext.Default, MainWindow.JsonResolver.Instance, ModelCatalogService.JsonMetadata);
    private static readonly JsonSerializerOptions DefaultOptions = CreateOptions(new());
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> OptionsCache = new();

    private static JsonSerializerOptions CreateOptions(JsonSerializerOptions source)
    {
        var options = new JsonSerializerOptions(source) { TypeInfoResolver = Resolver };
        options.MakeReadOnly();
        return options;
    }

    private static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions? options) =>
        (JsonTypeInfo<T>)(options is null ? DefaultOptions : OptionsCache.GetValue(options, CreateOptions))
            .GetTypeInfo(typeof(T));

    public static string Serialize<T>(T value, JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(value, TypeInfo<T>(options));
    public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(json, TypeInfo<T>(options));
    public static T? Deserialize<T>(Stream stream, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(stream, TypeInfo<T>(options));
    public static ValueTask<T?> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default) =>
        JsonSerializer.DeserializeAsync(stream, TypeInfo<T>(null), cancellationToken);
    public static Task SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(stream, value, TypeInfo<T>(null), cancellationToken);
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppBackupPayload))]
[JsonSerializable(typeof(AstraCoreManifest))]
[JsonSerializable(typeof(MediaAudioTrack))]
[JsonSerializable(typeof(List<MediaAudioTrack>))]
[JsonSerializable(typeof(EmbeddedSubtitleTrack))]
[JsonSerializable(typeof(List<EmbeddedSubtitleTrack>))]
[JsonSerializable(typeof(SceneDetectionResult))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object>[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
internal partial class AppJsonContext : JsonSerializerContext;

public sealed record MediaAudioTrack(long Id, string Title, string Language, bool IsDefault);
public sealed record EmbeddedSubtitleTrack(int Index, string CodecName, string Language, string Title);
public sealed record SceneDetectionResult(IReadOnlyList<double> CutSeconds);
