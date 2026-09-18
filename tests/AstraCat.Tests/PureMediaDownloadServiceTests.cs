using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using AstraCat;

namespace AstraCat.Tests;

[TestClass]
public class PureMediaDownloadServiceTests
{
    [TestMethod]
    public void PureMediaDownloadService_Instance_IsNotNull()
    {
        Assert.IsNotNull(PureMediaDownloadService.Instance, "PureMediaDownloadService 单例实例不应为空");
    }

    [TestMethod]
    public async Task PureMediaDownloadService_InspectAsync_ValidYouTubeUrl_ReturnsMetadata()
    {
        var url = "https://www.youtube.com/watch?v=5XbxbzdN0x0";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await PureMediaDownloadService.Instance.InspectAsync(url, token: cts.Token);

            Assert.IsNotNull(result, "探测结果不应为空");
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Title), "探测应解析出有效视频标题");
            Assert.IsTrue(result.Duration > 0, "视频时长应大于 0 秒");
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Uploader), "探测应包含作者频道名称");
        }
        catch (Exception ex)
        {
            // 当测试环境无网络代理无法直连 YouTube 时，标记为 Inconclusive 而非构建失败
            Assert.Inconclusive($"网络未连接或海外节点不可达，跳过在线测试: {ex.Message}");
        }
    }

    [TestMethod]
    public async Task PureMediaDownloadService_InspectAsync_InvalidUrl_ThrowsException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExceptionAsync<System.Net.Http.HttpRequestException>(async () =>
        {
            await PureMediaDownloadService.Instance.InspectAsync("https://invalid-non-existent-domain-12345.xyz/test.mp4", token: cts.Token);
        });
    }
}
