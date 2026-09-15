using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AstraCat;

public sealed partial class ModelCatalogService
{
    internal static IJsonTypeInfoResolver JsonMetadata => CatalogJsonContext.Default;

    internal static void VerifyJsonContract()
    {
        var value = AotJson.Deserialize<CachedCatalog>("{\"RefreshedAt\":\"2026-09-14T00:00:00Z\",\"Models\":[]}")!;
        using var stream = new MemoryStream();
        AotJson.SerializeAsync(stream, value).GetAwaiter().GetResult();
        stream.Position = 0;
        var restored = AotJson.DeserializeAsync<CachedCatalog>(stream).GetAwaiter().GetResult();
        if (restored?.RefreshedAt != value.RefreshedAt || restored.Models.Count != 0)
            throw new InvalidDataException("Model catalog async roundtrip failed.");
    }

    [JsonSerializable(typeof(CachedCatalog))]
    private partial class CatalogJsonContext : JsonSerializerContext;
}
