using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraCat.Services.Speech;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AstraCat.Tests;

[TestClass]
public class SherpaSpeechEngineTests
{
    [TestMethod]
    public void SherpaModelRegistry_ContainsCoreVendorsAndModels()
    {
        var allProfiles = SherpaModelRegistry.GetAllProfiles();
        Assert.IsTrue(allProfiles.Count >= 8, $"应包含至少 8 个预设模型画像，实际包含 {allProfiles.Count} 个");

        var vendors = allProfiles.Select(p => p.Vendor).Distinct().ToList();
        CollectionAssert.Contains(vendors, AsrModelVendor.OpenAI);
        CollectionAssert.Contains(vendors, AsrModelVendor.Alibaba);
        CollectionAssert.Contains(vendors, AsrModelVendor.Nvidia);

        // FeatureDim checks
        var qwen = allProfiles.First(p => p.Family == AsrModelFamily.Qwen3Asr);
        Assert.AreEqual(128, qwen.FeatureDim, "Qwen3-ASR 必须指定 128 维特征");

        var whisper = allProfiles.First(p => p.Family == AsrModelFamily.Whisper);
        Assert.AreEqual(80, whisper.FeatureDim, "Whisper 必须指定 80 维特征");

        var parakeet = allProfiles.First(p => p.Family == AsrModelFamily.NeMoCtc);
        Assert.AreEqual(80, parakeet.FeatureDim, "NeMo Parakeet 必须指定 80 维特征");
    }

    [TestMethod]
    public void SherpaModelRegistry_TryGetProfile_ResolvesAliases()
    {
        // 1. OpenAI Whisper variants
        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("whisper-tiny", out var tinyProfile));
        Assert.IsNotNull(tinyProfile);
        Assert.AreEqual("whisper-tiny", tinyProfile.Id);

        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("whisper-v3-turbo", out var turboProfile));
        Assert.IsNotNull(turboProfile);
        Assert.AreEqual("whisper-v3-turbo", turboProfile.Id);

        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("whisper-large-v3-turbo", out var turboAlias));
        Assert.IsNotNull(turboAlias);
        Assert.AreEqual("whisper-v3-turbo", turboAlias.Id);

        // 2. Alibaba Qwen variants
        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("qwen-0.6b", out var qwen06));
        Assert.IsNotNull(qwen06);
        Assert.AreEqual("qwen-0.6b", qwen06.Id);

        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("qwen3-asr-0.6b", out var qwen06Alias));
        Assert.IsNotNull(qwen06Alias);
        Assert.AreEqual("qwen-0.6b", qwen06Alias.Id);

        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("qwen-1.7b", out var qwen17));
        Assert.IsNotNull(qwen17);
        Assert.AreEqual("qwen-1.7b", qwen17.Id);

        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("qwen3-asr-1.7b", out var qwen17Alias));
        Assert.IsNotNull(qwen17Alias);
        Assert.AreEqual("qwen-1.7b", qwen17Alias.Id);

        // 3. NVIDIA NeMo variants
        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("nvidia-parakeet-v3", out var parakeet));
        Assert.IsNotNull(parakeet);
        Assert.AreEqual("nvidia-parakeet-v3", parakeet.Id);

        Assert.IsTrue(SherpaModelRegistry.TryGetProfile("nvidia-parakeet-tdt-0.6b-v3", out var parakeetAlias));
        Assert.IsNotNull(parakeetAlias);
        Assert.AreEqual("nvidia-parakeet-v3", parakeetAlias.Id);

        // 4. Invalid ID
        Assert.IsFalse(SherpaModelRegistry.TryGetProfile("unknown-dummy-model-xyz", out _));
        Assert.IsFalse(SherpaModelRegistry.TryGetProfile("", out _));
        Assert.IsFalse(SherpaModelRegistry.TryGetProfile(null!, out _));
    }

    [TestMethod]
    public void SherpaModelLocator_PathsAndVad_AreHandledGracefully()
    {
        var root = SherpaModelLocator.GetModelRoot();
        Assert.IsFalse(string.IsNullOrWhiteSpace(root));

        // Locate VAD without throwing
        var vadPath = SherpaModelLocator.FindSileroVadModel();
        // vadPath can be null or a real path, but must not crash

        // Non-existent model profile check
        var dummyProfile = new SherpaModelProfile
        {
            Id = "dummy-nonexistent-model",
            DisplayName = "Dummy",
            Vendor = AsrModelVendor.OpenAI,
            Family = AsrModelFamily.Whisper,
            Description = "Test",
            SizeText = "0 MB"
        };
        Assert.IsFalse(SherpaModelLocator.IsModelInstalled(dummyProfile, out var paths));
        Assert.IsNull(paths);
    }

    [TestMethod]
    public void SherpaSpeechEngine_DisposeAndLifecycle_Succeeds()
    {
        using var engine = new SherpaSpeechEngine();
        // Double dispose safe
        engine.Dispose();
    }

    [TestMethod]
    public async Task SherpaSpeechEngine_TranscribeNonExistentMedia_ThrowsFileNotFoundException()
    {
        using var engine = new SherpaSpeechEngine();
        var progress = new Progress<(int Percent, string Message, string? LogLine)>();
        var nonExistent = Path.Combine(Path.GetTempPath(), $"missing_test_{Guid.NewGuid():N}.wav");

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(async () =>
        {
            await engine.TranscribeMediaAsync(
                nonExistent,
                "whisper-tiny",
                new SherpaTranscriptionOptions(),
                progress,
                CancellationToken.None);
        });
    }

    [TestMethod]
    public async Task SherpaSpeechEngine_TranscribeUnknownModel_ThrowsNotSupportedException()
    {
        using var engine = new SherpaSpeechEngine();
        var progress = new Progress<(int Percent, string Message, string? LogLine)>();

        // Create a temporary dummy wav file
        var tempWav = Path.Combine(Path.GetTempPath(), $"dummy_{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(tempWav, new byte[100]);
            await Assert.ThrowsExceptionAsync<NotSupportedException>(async () =>
            {
                await engine.TranscribeMediaAsync(
                    tempWav,
                    "invalid-unknown-model-id",
                    new SherpaTranscriptionOptions(),
                    progress,
                    CancellationToken.None);
            });
        }
        finally
        {
            try { File.Delete(tempWav); } catch { }
        }
    }

    [TestMethod]
    public async Task SherpaSpeechEngine_RealInference_IfModelInstalled()
    {
        // Check if any registered model is installed locally
        var installedProfile = SherpaModelRegistry.GetAllProfiles()
            .FirstOrDefault(p => SherpaModelLocator.IsModelInstalled(p, out _));

        if (installedProfile is null)
        {
            Assert.Inconclusive("本地尚未下载任何 Sherpa ONNX 模型权重，跳过实机推理用例。");
            return;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AstraCat.csproj")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "testdata", "asr_zh.wav"),
            Path.Combine(Environment.CurrentDirectory, "runtime", "testdata", "asr_zh.wav"),
        };
        if (repoRoot is not null)
        {
            candidates.Add(Path.Combine(repoRoot, "runtime", "testdata", "asr_zh.wav"));
            candidates.Add(Path.Combine(repoRoot, "artifacts", "media-chain-tests", "asr-16k-mono.wav"));
        }

        var audioPath = candidates.FirstOrDefault(File.Exists);
        if (audioPath is null)
        {
            Assert.Inconclusive("未找到测试音频文件 (runtime/testdata/asr_zh.wav)，跳过实机推理。");
            return;
        }

        using var engine = new SherpaSpeechEngine();
        var progress = new Progress<(int Percent, string Message, string? LogLine)>();
        var result = await engine.TranscribeMediaAsync(
            audioPath,
            installedProfile.Id,
            new SherpaTranscriptionOptions { EnableVad = false },
            progress,
            CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Segments.Count > 0, "转录应产生至少一条有效字幕片段");
        foreach (var seg in result.Segments)
        {
            Assert.IsTrue(seg.EndMilliseconds >= seg.StartMilliseconds, "字幕结束时间不能小于起始时间");
            Assert.IsFalse(string.IsNullOrWhiteSpace(seg.Text), "字幕文本不能为空");
        }
    }
}
