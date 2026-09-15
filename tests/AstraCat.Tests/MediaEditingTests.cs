using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Threading.Tasks;
using AstraCat;

namespace AstraCat.Tests;

[TestClass]
public class MediaEditingTests
{
    [TestMethod]
    public async Task TrimMediaAsync_NonExistentFile_ReturnsFalseSafely()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "non_existent_" + System.Guid.NewGuid() + ".mp4");
        var target = Path.Combine(Path.GetTempPath(), "target_" + System.Guid.NewGuid() + ".mp4");
        var result = await MediaExportService.TrimMediaAsync(nonExistent, target, 0, 5);
        Assert.IsFalse(result, "对不存在的输入文件，TrimMediaAsync 应安全返回 false");
    }

    [TestMethod]
    public async Task ChangeAudioSpeedAsync_NonExistentFile_ReturnsFalseSafely()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "non_existent_" + System.Guid.NewGuid() + ".wav");
        var target = Path.Combine(Path.GetTempPath(), "target_" + System.Guid.NewGuid() + ".wav");
        var result = await MediaExportService.ChangeAudioSpeedAsync(nonExistent, target, 1.5);
        Assert.IsFalse(result, "对不存在的输入文件，ChangeAudioSpeedAsync 应安全返回 false");
    }

    [TestMethod]
    public async Task ExtractAudioStreamAsync_NonExistentFile_ReturnsFalseSafely()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "non_existent_" + System.Guid.NewGuid() + ".mp4");
        var target = Path.Combine(Path.GetTempPath(), "target_" + System.Guid.NewGuid() + ".m4a");
        var result = await MediaExportService.ExtractAudioStreamAsync(nonExistent, target);
        Assert.IsFalse(result, "对不存在的输入文件，ExtractAudioStreamAsync 应安全返回 false");
    }

    [TestMethod]
    public void AstraCoreNative_TryChangeAudioSpeed_InvalidSpeedBounds_Fails()
    {
        var okTooSlow = AstraCoreNative.TryChangeAudioSpeed("dummy.wav", "out.wav", 0.1, System.Threading.CancellationToken.None, out var errSlow);
        Assert.IsFalse(okTooSlow);
        Assert.IsNotNull(errSlow);

        var okTooFast = AstraCoreNative.TryChangeAudioSpeed("dummy.wav", "out.wav", 5.0, System.Threading.CancellationToken.None, out var errFast);
        Assert.IsFalse(okTooFast);
        Assert.IsNotNull(errFast);
    }
}
