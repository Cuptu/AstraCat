using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AstraCat;

public sealed class AppSettings
{
    // General & System
    public string Language { get; set; } = "zh-CN";
    public string ModelDownloadSource { get; set; } = "Auto";
    public string CudaRuntimeVersion { get; set; } = "12.8";
    public bool PreventSleep { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;

    // Speech Recognition & Segmentation Preferences (ASR & Split)
    public string DefaultTranscriptionModelId { get; set; } = "qwen-0.6b";
    public string DefaultSourceLanguage { get; set; } = "Auto";
    public string DefaultComputeDevice { get; set; } = "自动选择";
    public string DefaultPrecision { get; set; } = "float16";
    public bool WordTimestampsDefault { get; set; } = true;
    public bool SmartSegmentationDefault { get; set; } = true;
    public int MaxCharsCjkDefault { get; set; } = 22;
    public int MaxWordsEnglishDefault { get; set; } = 16;
    public bool VadFilterDefault { get; set; } = true;
    public double VadThreshold { get; set; } = 0.3;
    public int VadMinSilenceMs { get; set; } = 2000;
    public int VadSpeechPadMs { get; set; } = 400;
    public bool ProofreadingDefault { get; set; } = true;
    public bool WebResearchDefault { get; set; } = false;
    public bool EmotionDefault { get; set; } = true;
}

public sealed class AppBackupPayload
{
    public AppSettings? AppSettings { get; set; }
    public object? TranslationSettings { get; set; }
    public object? AsrSettings { get; set; }
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public string Version { get; set; } = "1.0.0";
}

public partial class MainWindow
{
    private AppSettings _appSettings = new();
    private bool _loadingSettings;
    private string _activeSettingsCategory = "all";
    private CancellationTokenSource _settingsCacheRefresh = new();
    private DateTime _settingsCacheMeasuredAt = DateTime.MinValue;
    private string? _settingsCacheMeasuredText;

    private string AppSettingsPath =>
        Path.Combine(_deployment.RuntimeRoot, "config", "app-settings.json");

    private string AsrSettingsPath =>
        Path.Combine(_deployment.RuntimeRoot, "config", "asr-settings.json");

    private string WaveformCacheDirectory =>
        Path.Combine(_deployment.RuntimeRoot, "cache", "waveforms");

    private string LogDirectory =>
        Path.Combine(_deployment.AppRoot, "Assets", "Brand");

    private void InitializeSettings()
    {
        LoadAppSettings();
        ApplyLoadedSettingsToUi();

        if (SettingsCardsScrollViewer != null)
        {
            SettingsCardsScrollViewer.ScrollChanged += SettingsCardsScrollViewer_OnScrollChanged;
        }
    }

    private void LoadAppSettings()
    {
        try
        {
            if (File.Exists(AppSettingsPath))
            {
                var json = File.ReadAllText(AppSettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    _appSettings = loaded;
                }
            }
        }
        catch
        {
            _appSettings = new AppSettings();
        }
    }

    private void SaveAppSettings()
    {
        if (_loadingSettings) return;

        try
        {
            var configDir = Path.GetDirectoryName(AppSettingsPath);
            if (!string.IsNullOrWhiteSpace(configDir))
                Directory.CreateDirectory(configDir);

            CaptureSettingsFromUi();

            var json = JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppSettingsPath, json);

            // Keep system sleep prevention in sync
            ApplySleepPreventionState(_appSettings.PreventSleep);
        }
        catch
        {
            // Ignore background save errors
        }
    }

    private void ApplyLoadedSettingsToUi()
    {
        _loadingSettings = true;
        try
        {
            // Language
            if (SettingsLanguageCombo != null)
                SettingsLanguageCombo.SelectedIndex = 0;

            // Download Mirror
            if (SettingsDownloadSourceCombo != null)
            {
                SettingsDownloadSourceCombo.SelectedIndex = _appSettings.ModelDownloadSource switch
                {
                    "HfMirror" => 1,
                    "HuggingFace" => 2,
                    _ => 0
                };
            }

            // CUDA
            if (SettingsCudaVersionCombo != null)
            {
                SettingsCudaVersionCombo.SelectedIndex = _appSettings.CudaRuntimeVersion switch
                {
                    "12.4" => 1,
                    "cpu" => 2,
                    _ => 0
                };
            }

            // Toggles
            if (SettingsPreventSleepToggle != null)
                SettingsPreventSleepToggle.IsChecked = _appSettings.PreventSleep;

            if (SettingsCheckUpdatesToggle != null)
                SettingsCheckUpdatesToggle.IsChecked = _appSettings.CheckUpdatesOnStartup;

            // Paths
            if (SettingsRuntimePathBox != null)
                SettingsRuntimePathBox.Text = _deployment.RuntimeRoot;

            if (SettingsProjectPathBox != null)
                SettingsProjectPathBox.Text = ProjectDataRoot;

            if (SettingsCachePathBox != null)
                SettingsCachePathBox.Text = Path.Combine(_deployment.RuntimeRoot, "cache");

        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void CaptureSettingsFromUi()
    {
        if (SettingsLanguageCombo != null)
            _appSettings.Language = "zh-CN";

        if (SettingsDownloadSourceCombo != null)
        {
            _appSettings.ModelDownloadSource = SettingsDownloadSourceCombo.SelectedIndex switch
            {
                1 => "HfMirror",
                2 => "HuggingFace",
                _ => "Auto"
            };
        }

        if (SettingsCudaVersionCombo != null)
        {
            _appSettings.CudaRuntimeVersion = SettingsCudaVersionCombo.SelectedIndex switch
            {
                1 => "12.4",
                2 => "cpu",
                _ => "12.8"
            };
        }

        if (SettingsPreventSleepToggle != null)
            _appSettings.PreventSleep = SettingsPreventSleepToggle.IsChecked == true;

        if (SettingsCheckUpdatesToggle != null)
            _appSettings.CheckUpdatesOnStartup = SettingsCheckUpdatesToggle.IsChecked == true;
    }

    private void RefreshSettingsHardwareAndCache()
    {
        // 1. GPU & CUDA hardware inspection
        _ = RefreshCudaDetailAsync();

        // 2. Cache Size Calculation
        RefreshCacheSizeDisplay();
    }

    private void RefreshCacheSizeDisplay(bool forceRefresh = false)
    {
        if (!forceRefresh && _settingsCacheMeasuredText is not null &&
            DateTime.UtcNow - _settingsCacheMeasuredAt < TimeSpan.FromSeconds(15))
        {
            SettingsCacheSizeText.Text = _settingsCacheMeasuredText;
            return;
        }

        var previous = _settingsCacheRefresh;
        var request = new CancellationTokenSource();
        _settingsCacheRefresh = request;
        previous.Cancel();
        previous.Dispose();
        _ = RefreshCacheSizeDisplayAsync(request);
    }

    private async Task RefreshCacheSizeDisplayAsync(CancellationTokenSource request)
    {
        try
        {
            var totalBytes = await Task.Run(() =>
            {
                long totalBytes = 0;
                var cacheRoot = Path.Combine(_deployment.RuntimeRoot, "cache");
                if (Directory.Exists(cacheRoot))
                {
                    foreach (var file in new DirectoryInfo(cacheRoot).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        request.Token.ThrowIfCancellationRequested();
                        totalBytes += file.Length;
                    }
                }
                return totalBytes;
            }, request.Token);

            if (!ReferenceEquals(request, _settingsCacheRefresh) || request.IsCancellationRequested || _isClosing) return;
            _settingsCacheMeasuredText = $"清除缓存 (约 {FormatByteSize(totalBytes)})";
            _settingsCacheMeasuredAt = DateTime.UtcNow;
            SettingsCacheSizeText.Text = _settingsCacheMeasuredText;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(request, _settingsCacheRefresh) && !_isClosing)
                SettingsCacheSizeText.Text = "清除缓存";
        }
    }



    #region Category navigation and scroll synchronization

    private bool _isProgrammaticScrolling;

    private void SettingsCategory_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string category) return;

        switch (category)
        {
            case "system":
                ScrollToSettingsCard(SettingsCardSystem, "system");
                break;
            case "storage":
                ScrollToSettingsCard(SettingsCardStorage, "storage");
                break;
            case "backup":
                ScrollToSettingsCard(SettingsCardBackup, "backup");
                break;
            case "about":
                ScrollToSettingsCard(SettingsCardAbout, "about");
                break;
            default:
                ScrollToSettingsCard(null, "all");
                break;
        }
    }

    private void ScrollToSettingsCard(Control? card, string category)
    {
        _activeSettingsCategory = category;
        UpdateSettingsNavSelection(category);

        if (SettingsCardsScrollViewer == null) return;

        _isProgrammaticScrolling = true;

        if (card == null || category == "all")
        {
            SettingsCardsScrollViewer.Offset = new Vector(0, 0);
        }
        else
        {
            var relativePoint = card.TranslatePoint(new Point(0, 0), SettingsCardsScrollViewer);
            if (relativePoint.HasValue)
            {
                var targetY = Math.Max(0, SettingsCardsScrollViewer.Offset.Y + relativePoint.Value.Y - 16);
                SettingsCardsScrollViewer.Offset = new Vector(0, targetY);
            }
            else
            {
                card.BringIntoView();
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            _isProgrammaticScrolling = false;
        }, DispatcherPriority.Background);
    }

    private void SettingsCardsScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticScrolling || SettingsCardsScrollViewer == null) return;

        var scrollY = SettingsCardsScrollViewer.Offset.Y;
        if (scrollY <= 50)
        {
            UpdateSettingsNavSelection("all");
            return;
        }

        // Determine which section is currently at the top of the viewport
        string bestCategory = "all";
        var cards = new (Control? Control, string Category)[]
        {
            (SettingsCardSystem, "system"),
            (SettingsCardStorage, "storage"),
            (SettingsCardBackup, "backup"),
            (SettingsCardAbout, "about")
        };

        foreach (var (ctrl, cat) in cards)
        {
            if (ctrl == null) continue;
            var pt = ctrl.TranslatePoint(new Point(0, 0), SettingsCardsScrollViewer);
            if (pt.HasValue && pt.Value.Y <= 200)
            {
                bestCategory = cat;
            }
        }

        UpdateSettingsNavSelection(bestCategory);
    }

    private void UpdateSettingsNavSelection(string category)
    {
        _activeSettingsCategory = category;
        if (SettingsCatAll != null) SettingsCatAll.Classes.Set("selected", category == "all");
        if (SettingsCatSystem != null) SettingsCatSystem.Classes.Set("selected", category == "system");
        if (SettingsCatStorage != null) SettingsCatStorage.Classes.Set("selected", category == "storage");
        if (SettingsCatBackup != null) SettingsCatBackup.Classes.Set("selected", category == "backup");
        if (SettingsCatAbout != null) SettingsCatAbout.Classes.Set("selected", category == "about");
    }

    #endregion

    #region Storage and directory actions

    private async void SettingsBrowseRuntimeRoot_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择统一存储目录",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selectedPath = folders[0].Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    if (SettingsRuntimePathBox != null)
                        SettingsRuntimePathBox.Text = selectedPath;
                    ShowSettingsNotice("存储目录已更新。如需完全生效，请重启应用以重新加载模型与依赖。");
                }
            }
        }
        catch (Exception ex)
        {
            ShowSettingsNotice($"选择目录失败：{ex.Message}", isError: true);
        }
    }

    private void SettingsOpenRuntimeRoot_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(_deployment.RuntimeRoot);
    }

    private void SettingsOpenProjectData_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(ProjectDataRoot);
    }

    private void SettingsOpenLogRoot_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(LogDirectory);
    }

    private async void SettingsClearCache_OnClick(object? sender, RoutedEventArgs e)
    {
        SettingsClearCacheButton.IsEnabled = false;
        _settingsCacheRefresh.Cancel();
        try
        {
            await Task.Run(() =>
            {
                var cacheRoot = Path.Combine(_deployment.RuntimeRoot, "cache");
                if (!Directory.Exists(cacheRoot)) return;
                var dir = new DirectoryInfo(cacheRoot);
                foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { file.Delete(); } catch { }
                }
                foreach (var subDir in dir.EnumerateDirectories())
                {
                    try { subDir.Delete(true); } catch { }
                }
            });

            _settingsCacheMeasuredAt = DateTime.MinValue;
            _settingsCacheMeasuredText = null;
            RefreshCacheSizeDisplay(forceRefresh: true);
            ShowSettingsNotice("缓存已成功清理！");
        }
        catch (Exception ex)
        {
            ShowSettingsNotice($"清理缓存失败：{ex.Message}", isError: true);
        }
        finally
        {
            SettingsClearCacheButton.IsEnabled = true;
        }
    }

    private static void OpenFolderInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore explorer opening errors
        }
    }

    #endregion

    #region Settings Event Handlers

    private void SettingsValue_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        SaveAppSettings();
    }

    private void SettingsToggle_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (e.Property == ToggleSwitch.IsCheckedProperty)
        {
            SaveAppSettings();
        }
    }

    #endregion

    #region Configuration import, export and reset

    private async void SettingsExportConfig_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            CaptureSettingsFromUi();

            object? translationData = null;
            if (File.Exists(TranslationSettingsPath))
            {
                try { translationData = JsonSerializer.Deserialize<object>(File.ReadAllText(TranslationSettingsPath)); } catch { }
            }

            object? asrData = null;
            if (File.Exists(AsrSettingsPath))
            {
                try { asrData = JsonSerializer.Deserialize<object>(File.ReadAllText(AsrSettingsPath)); } catch { }
            }

            var payload = new AppBackupPayload
            {
                AppSettings = _appSettings,
                TranslationSettings = translationData,
                AsrSettings = asrData,
                ExportedAt = DateTimeOffset.Now,
                Version = "1.0.0"
            };

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出应用配置备份",
                SuggestedFileName = $"AstraCat-Config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",
                ShowOverwritePrompt = true
            });

            if (file != null)
            {
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json);

                ShowSettingsNotice("配置导出成功！");
            }
        }
        catch (Exception ex)
        {
            ShowSettingsNotice($"导出失败：{ex.Message}", isError: true);
        }
    }

    private async void SettingsImportConfig_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择应用配置备份文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON 配置文件 (*.json)") { Patterns = new[] { "*.json" } }
                }
            });

            if (files != null && files.Count > 0)
            {
                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();

                var payload = JsonSerializer.Deserialize<AppBackupPayload>(json);
                if (payload != null)
                {
                    if (payload.AppSettings != null)
                    {
                        _appSettings = payload.AppSettings;
                        SaveAppSettings();
                        ApplyLoadedSettingsToUi();
                    }

                    if (payload.TranslationSettings != null)
                    {
                        var transJson = JsonSerializer.Serialize(payload.TranslationSettings, new JsonSerializerOptions { WriteIndented = true });
                        Directory.CreateDirectory(Path.GetDirectoryName(TranslationSettingsPath)!);
                        File.WriteAllText(TranslationSettingsPath, transJson);
                        LoadTranslationSettings();
                        RebuildTranslationProviderList();
                    }

                    if (payload.AsrSettings != null)
                    {
                        var asrJson = JsonSerializer.Serialize(payload.AsrSettings, new JsonSerializerOptions { WriteIndented = true });
                        Directory.CreateDirectory(Path.GetDirectoryName(AsrSettingsPath)!);
                        File.WriteAllText(AsrSettingsPath, asrJson);
                    }

                    ShowSettingsNotice("配置已成功导入并恢复！");
                }
            }
        }
        catch (Exception ex)
        {
            ShowSettingsNotice($"导入失败：{ex.Message}", isError: true);
        }
    }

    private async void SettingsResetDefaults_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await PromptConfirmResetAsync();
        if (!confirmed) return;

        try
        {
            // Reset App Settings
            _appSettings = new AppSettings();
            SaveAppSettings();
            ApplyLoadedSettingsToUi();

            // Reset Model configuration defaults
            ResetModelConfigurationDefaults();

            // Delete / Reset translation settings to defaults
            if (File.Exists(TranslationSettingsPath))
            {
                try { File.Delete(TranslationSettingsPath); } catch { }
            }
            LoadTranslationSettings();
            RebuildTranslationProviderList();

            // Reset ASR defaults
            if (File.Exists(AsrSettingsPath))
            {
                try { File.Delete(AsrSettingsPath); } catch { }
            }

            ShowSettingsNotice("所有设置已成功恢复为默认值！");
        }
        catch (Exception ex)
        {
            ShowSettingsNotice($"重置失败：{ex.Message}", isError: true);
        }
    }

    private async Task<bool> PromptConfirmResetAsync()
    {
        return await ConfirmComponentUninstallAsync(
            "恢复默认配置？",
            "将重置所有通用设置、硬件加速选项、存储路径与翻译/ASR 配置为默认初始状态。此操作不可撤销，已下载的模型文件不会被删除。",
            "确认恢复");
    }

    private void ShowSettingsNotice(string message, bool isError = false)
    {
        if (SettingsNoticeBanner == null || SettingsNoticeText == null) return;

        SettingsNoticeText.Text = message;
        SettingsNoticeBanner.Background = isError ? Brush.Parse("#FEF2F2") : Brush.Parse("#F0FDF4");
        SettingsNoticeBanner.BorderBrush = isError ? Brush.Parse("#FCA5A5") : Brush.Parse("#86EFAC");
        SettingsNoticeText.Foreground = isError ? Brush.Parse("#DC2626") : Brush.Parse("#16A34A");
        SettingsNoticeBanner.IsVisible = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SettingsNoticeBanner.IsVisible = false;
        };
        timer.Start();
    }

    #endregion

    #region About and support actions

    private void SettingsCheckUpdate_OnClick(object? sender, RoutedEventArgs e)
    {
        ShowSettingsNotice("当前已是最新版本 (v1.0.0)！");
    }

    private void SettingsOpenGithub_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenUrlInBrowser("https://github.com/Cuptu/AstraCat");
    }

    private void SettingsOpenFeedback_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenUrlInBrowser("https://github.com/Cuptu/AstraCat/issues");
    }

    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser opening errors
        }
    }

    #endregion

    #region System Sleep Prevention (Windows Execution State)

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private void ApplySleepPreventionState(bool preventSleep)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (preventSleep)
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            }
            else
            {
                SetThreadExecutionState(ES_CONTINUOUS);
            }
        }
        catch
        {
            // Ignore execution state errors
        }
    }

    #region Project <-> Global Defaults Synchronization

    private void SaveCurrentProjectAsGlobalDefaults(CaptionProject project)
    {
        if (project == null) return;

        if (!string.IsNullOrWhiteSpace(project.TranscriptionModelId))
            _appSettings.DefaultTranscriptionModelId = project.TranscriptionModelId;

        _appSettings.SmartSegmentationDefault = project.EnableLlmSegmentation;
        _appSettings.ProofreadingDefault = project.EnableSubtitleProofreading;
        _appSettings.WebResearchDefault = project.EnableWebTerminologyResearch;

        SaveAppSettings();
        ApplyLoadedSettingsToUi();
        ShowSettingsNotice("当前项目转录配置已成功保存为全局默认！");
    }

    private void ResetProjectToGlobalDefaults(CaptionProject project)
    {
        if (project == null) return;

        if (!string.IsNullOrWhiteSpace(_appSettings.DefaultTranscriptionModelId))
            project.TranscriptionModelId = _appSettings.DefaultTranscriptionModelId;

        project.EnableLlmSegmentation = _appSettings.SmartSegmentationDefault;
        project.EnableSubtitleProofreading = _appSettings.ProofreadingDefault;
        project.EnableWebTerminologyResearch = _appSettings.WebResearchDefault;

        SaveProjects();
        ShowSettingsNotice("↺ 已恢复为全局默认转录配置！");
    }

    #endregion

    #endregion
}
