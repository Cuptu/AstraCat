using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using AstraCat;

namespace AstraCat.Tests;

[TestClass]
public class DownloadWorkerTests
{
    [TestMethod]
    public void DownloadWorkerClient_IsAvailable_WhenPythonAndWorkerExist()
    {
        var available = DownloadWorkerClient.IsAvailable();
        Assert.IsTrue(available, "内置 Python 与 download_worker.py 应就绪可用");
    }

    [TestMethod]
    public async Task DownloadWorkerClient_PingAsync_ReturnsTrue()
    {
        using var client = new DownloadWorkerClient();
        var ok = await client.PingAsync(CancellationToken.None);
        Assert.IsTrue(ok, "向 download_worker 发送 ping 请求应返回 ok=true");
    }

    [TestMethod]
    public async Task DownloadWorkerClient_InspectAsync_InvalidUrl_ThrowsExpectedException()
    {
        using var client = new DownloadWorkerClient();
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await client.InspectAsync("invalid://not-a-valid-url", token: CancellationToken.None);
        });
        Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Message), "非法 URL 探测应返回描述清晰的错误信息");
    }
}
