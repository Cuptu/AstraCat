using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AstraCat;

internal readonly record struct AsrWorkerProgress(int Percent, string Message, string? LogLine);

/// <summary>
/// Owns one warm ASR worker at a time. The Python protocol is intentionally
/// multi-request, so keeping the process alive lets it reuse imported modules
/// and the currently loaded model. A short idle timeout releases RAM/VRAM.
/// </summary>
internal sealed class AsrWorkerClient(DeploymentManager deployment) : IDisposable
{
    private const int ErrorTailLimit = 128 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _processSync = new();
    private readonly object _errorSync = new();
    private readonly StringBuilder _errorTail = new();
    private Process? _process;
    private string? _runtimeId;
    private Task? _stderrPump;
    private CancellationTokenSource? _idleShutdown;
    private bool _disposed;

    public async Task<string> TranscribeAsync(
        string runtimeId,
        string workerPath,
        string requestJson,
        IProgress<AsrWorkerProgress> progress,
        CancellationToken token)
    {
        await _requestGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            CancelIdleShutdown();
            ClearErrorTail();
            var process = EnsureStarted(runtimeId, workerPath);
            using var cancellation = token.Register(() => KillWorker(process));

            try
            {
                await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                while (await process.StandardOutput.ReadLineAsync(token) is { } outputLine)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(outputLine);
                        var root = document.RootElement;
                        if (root.TryGetProperty("event", out var eventElement) &&
                            string.Equals(eventElement.GetString(), "progress", StringComparison.OrdinalIgnoreCase))
                        {
                            progress.Report(new AsrWorkerProgress(
                                root.TryGetProperty("percent", out var percent) ? percent.GetInt32() : 0,
                                root.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                                root.TryGetProperty("log", out var log) ? log.GetString() : null));
                            continue;
                        }

                        if (root.TryGetProperty("ok", out _))
                        {
                            ScheduleIdleShutdown();
                            return outputLine;
                        }
                    }
                    catch (JsonException)
                    {
                        // Some third-party libraries write banners to stdout.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                KillWorker(process);
                throw;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // Killing a redirected process can surface as IOException or
                // ObjectDisposedException before the async read observes its token.
                // Preserve cancellation semantics for the caller in either race.
                KillWorker(process);
                throw new OperationCanceledException(token);
            }
            catch
            {
                KillWorker(process);
                throw;
            }

            token.ThrowIfCancellationRequested();
            KillWorker(process);
            var error = ErrorTail();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "识别进程没有返回结果"
                : error);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private Process EnsureStarted(string runtimeId, string workerPath)
    {
        lock (_processSync)
        {
            if (_process is { HasExited: false } running &&
                string.Equals(_runtimeId, runtimeId, StringComparison.OrdinalIgnoreCase))
                return running;

            StopWorkerLocked();
            var pythonPath = deployment.GetRuntimePythonExecutable(runtimeId);
            if (!File.Exists(pythonPath) || !File.Exists(workerPath))
                throw new FileNotFoundException("本地识别环境不完整，请在模型配置中修复运行环境");

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                WorkingDirectory = deployment.AppRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add(workerPath);
            startInfo.Environment["ASTRACAT_MODEL_HOME"] = deployment.ModelRoot;
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            deployment.ConfigureCudaEnvironment(startInfo);

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动本地语音识别进程");
            }

            _process = process;
            _runtimeId = runtimeId;
            _stderrPump = PumpStandardErrorAsync(process);
            return process;
        }
    }

    private async Task PumpStandardErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_errorSync)
                {
                    _errorTail.AppendLine(line);
                    if (_errorTail.Length > ErrorTailLimit)
                        _errorTail.Remove(0, _errorTail.Length - ErrorTailLimit);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    private void ScheduleIdleShutdown()
    {
        CancelIdleShutdown();
        var cancellation = new CancellationTokenSource();
        _idleShutdown = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IdleTimeout, cancellation.Token);
                await _requestGate.WaitAsync(cancellation.Token);
                try
                {
                    lock (_processSync) StopWorkerLocked();
                }
                finally
                {
                    _requestGate.Release();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void CancelIdleShutdown()
    {
        var previous = Interlocked.Exchange(ref _idleShutdown, null);
        if (previous is null) return;
        previous.Cancel();
        previous.Dispose();
    }

    private void KillWorker(Process process)
    {
        lock (_processSync)
        {
            if (!ReferenceEquals(process, _process)) return;
            StopWorkerLocked();
        }
    }

    private void StopWorkerLocked()
    {
        var process = _process;
        _process = null;
        _runtimeId = null;
        _stderrPump = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        process.Dispose();
    }

    private void ClearErrorTail()
    {
        lock (_errorSync) _errorTail.Clear();
    }

    private string ErrorTail()
    {
        lock (_errorSync) return _errorTail.ToString().Trim();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelIdleShutdown();
        lock (_processSync) StopWorkerLocked();
        // An in-flight request releases this gate from its finally block. The process
        // lifetime is the real resource to close here; disposing the gate could race
        // with window shutdown and turn cancellation into ObjectDisposedException.
    }
}
