using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AstraCat;

public partial class MainWindow
{
    // 上下文嵌套在 MainWindow 内，以访问私有持久化 DTO，不扩大其公共接口。
    internal static class JsonResolver
    {
        internal static IJsonTypeInfoResolver Instance => WindowJsonContext.Default;
    }

    internal static void VerifyJsonContracts()
    {
        var projects = AotJson.Deserialize<List<CaptionProject>>(
            "[{\"Id\":\"fixture\",\"Name\":\"字幕测试\",\"SubtitleTrackCount\":2}]")!;
        var roundtrip = AotJson.Deserialize<List<CaptionProject>>(AotJson.Serialize(projects))!;
        if (roundtrip[0].Name != "字幕测试" || roundtrip[0].SubtitleTrackCount != 2)
            throw new InvalidDataException("Project JSON roundtrip failed.");
        var cues = new[] { new WorkspaceAutoSaveCue(1, 9007199254740993L, 9007199254740995L,
            "中文\nEnglish", "译文", 1, "group", "测试") };
        var saved = AotJson.Deserialize<List<WorkspaceAutoSaveCue>>(AotJson.Serialize(cues))!;
        if (saved[0] != cues[0]) throw new InvalidDataException("Cue precision/text roundtrip failed.");
        var insensitive = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (AotJson.Deserialize<List<TranslationBatchItem>>("[{\"id\":7,\"text\":\"译文\"}]", insensitive)![0].Id != 7)
            throw new InvalidDataException("Translation wire casing failed.");
        if (AotJson.Deserialize<List<SegmentationBatchItem>>("[{\"id\":7,\"parts\":[\"一句\"]}]", insensitive)![0].Parts.Count != 1)
            throw new InvalidDataException("Segmentation wire contract failed.");
        var research = AotJson.Deserialize<TerminologySearchBatch>("{\"summary\":\"摘要\",\"terms\":[]}", insensitive)!;
        if (research.Summary != "摘要") throw new InvalidDataException("Terminology wire contract failed.");
        _ = AotJson.Serialize(new TerminologyResearchDocument(), TerminologyJsonOptions);
        _ = AotJson.Deserialize<TranslationModelSettings>(AotJson.Serialize(new TranslationModelSettings()));
        _ = AotJson.Deserialize<List<SubtitleSegment>>(AotJson.Serialize(new List<SubtitleSegment> { new() { Original = "原文" } }));
        _ = AotJson.Deserialize<List<WorkspaceCueState>>(AotJson.Serialize(new[] { new WorkspaceCueState { Index = 1 } }));
        foreach (var protocol in new[] { "anthropic", "google", "ollama", "openai-responses", "openai-chat" })
        {
            var payload = BuildProviderTextPayload(new TranslationProviderProfile
            {
                Protocol = protocol, Model = "fixture", BaseUrl = "https://deepseek.com",
                ReasoningSummary = true, DeveloperRole = true
            }, "instruction", "input", 0.2, 512);
            using var parsed = System.Text.Json.JsonDocument.Parse(AotJson.Serialize(payload));
            var root = parsed.RootElement;
            if (protocol != "google" && root.GetProperty("model").GetString() != "fixture")
                throw new InvalidDataException("Provider request contract failed: " + protocol);
            if (protocol == "google" && root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32() != 512)
                throw new InvalidDataException("Google request contract failed.");
        }
    }

    internal void VerifyCompiledSubtitleBindings()
    {
        foreach (var property in new[] { nameof(SubtitleSegment.Original), nameof(SubtitleSegment.Translated) })
        {
            var segment = new SubtitleSegment { Original = "原文", Translated = "译文" };
            var cell = CreateSubtitleCell(property);
            cell.DataContext = segment;
            if (cell.Text != (property == nameof(SubtitleSegment.Original) ? "原文" : "译文"))
                throw new InvalidDataException("Compiled binding initial read failed.");
            cell.Text = "修改";
            if ((property == nameof(SubtitleSegment.Original) ? segment.Original : segment.Translated) != "修改")
                throw new InvalidDataException("Compiled binding writeback failed.");
            if (property == nameof(SubtitleSegment.Original)) segment.Original = "更新";
            else segment.Translated = "更新";
            if (cell.Text != "更新") throw new InvalidDataException("Compiled binding notification failed.");
            cell.DataContext = null;
        }
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(TranslationModelSettings))]
    [JsonSerializable(typeof(List<CaptionProject>))]
    [JsonSerializable(typeof(ObservableCollection<CaptionProject>))]
    [JsonSerializable(typeof(List<SubtitleSegment>))]
    [JsonSerializable(typeof(ObservableCollection<SubtitleSegment>))]
    [JsonSerializable(typeof(List<TranslationBatchItem>))]
    [JsonSerializable(typeof(List<SegmentationBatchItem>))]
    [JsonSerializable(typeof(TerminologyResearchDocument))]
    [JsonSerializable(typeof(TerminologySearchBatch))]
    [JsonSerializable(typeof(List<WorkspaceAutoSaveCue>))]
    [JsonSerializable(typeof(WorkspaceAutoSaveCue[]))]
    [JsonSerializable(typeof(List<WorkspaceCueState>))]
    [JsonSerializable(typeof(WorkspaceCueState[]))]
    private partial class WindowJsonContext : JsonSerializerContext;
}
