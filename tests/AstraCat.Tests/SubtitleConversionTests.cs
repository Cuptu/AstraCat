using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using AstraCat;

namespace AstraCat.Tests;

[TestClass]
public class SubtitleConversionTests
{
    [TestMethod]
    public void TryConvertSubtitleFormatInMemory_ValidSrtToAss_Succeeds()
    {
        var srt = @"1
00:00:01,234 --> 00:00:04,567
Hello World!

2
00:00:05,10 --> 00:00:08,200
Second line of subtitles
";
        var tempSrt = Path.GetTempFileName() + ".srt";
        var tempAss = Path.GetTempFileName() + ".ass";
        try
        {
            File.WriteAllText(tempSrt, srt);
            var converted = MediaExportService.TryConvertSubtitleFormatInMemory(tempSrt, tempAss);
            Assert.IsTrue(converted, "内存转换应成功执行");
            Assert.IsTrue(File.Exists(tempAss));

            var assContent = File.ReadAllText(tempAss);
            StringAssert.Contains(assContent, "[Script Info]");
            StringAssert.Contains(assContent, "[V4+ Styles]");
            StringAssert.Contains(assContent, "[Events]");
            StringAssert.Contains(assContent, "Dialogue: 0,0:00:01.23,0:00:04.56,Default,,0,0,0,,Hello World!");
            StringAssert.Contains(assContent, "Second line of subtitles");
        }
        finally
        {
            if (File.Exists(tempSrt)) File.Delete(tempSrt);
            if (File.Exists(tempAss)) File.Delete(tempAss);
        }
    }

    [TestMethod]
    public void TryConvertSubtitleFormatInMemory_DotMilliseconds_Succeeds()
    {
        var srt = @"1
00:01:02.300 --> 00:01:05.800
Timestamp with dots
";
        var tempSrt = Path.GetTempFileName() + ".srt";
        var tempAss = Path.GetTempFileName() + ".ass";
        try
        {
            File.WriteAllText(tempSrt, srt);
            var converted = MediaExportService.TryConvertSubtitleFormatInMemory(tempSrt, tempAss);
            Assert.IsTrue(converted);
            var assContent = File.ReadAllText(tempAss);
            StringAssert.Contains(assContent, "Dialogue: 0,0:01:02.30,0:01:05.80,Default,,0,0,0,,Timestamp with dots");
        }
        finally
        {
            if (File.Exists(tempSrt)) File.Delete(tempSrt);
            if (File.Exists(tempAss)) File.Delete(tempAss);
        }
    }

    [TestMethod]
    public void TryConvertSubtitleFormatInMemory_NonExistentFile_ReturnsFalse()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "non_existent_file_" + System.Guid.NewGuid() + ".srt");
        var targetAss = Path.Combine(Path.GetTempPath(), "target_" + System.Guid.NewGuid() + ".ass");
        var converted = MediaExportService.TryConvertSubtitleFormatInMemory(nonExistent, targetAss);
        Assert.IsFalse(converted, "不存在的文件应安全返回 false 而非崩溃");
    }
}
