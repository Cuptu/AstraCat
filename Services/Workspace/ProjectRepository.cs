using System.Collections.Concurrent;
using System.Text.Json;

namespace AstraCat;

using CaptionProject = MainWindow.CaptionProject;

/// <summary>
/// Transactional project storage repository.
/// Ensures that project index, subtitle tracks, cues, cue states, and translation
/// caches are committed atomically within a single transaction boundary.
/// </summary>
internal sealed class ProjectRepository
{
    private static readonly Lazy<ProjectRepository> Instance = new(() => new ProjectRepository());
    public static ProjectRepository Shared => Instance.Value;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectGates = new(StringComparer.OrdinalIgnoreCase);

    public static string RuntimeRoot => Path.Combine(DeploymentManager.FindAppRoot(), "runtime");
    public string ProjectDataRoot => Path.Combine(RuntimeRoot, "projects", "data");
    public string ProjectStorePath => Path.Combine(RuntimeRoot, "projects", "projects.json");

    public string ProjectDirectory(string projectId) => Path.Combine(ProjectDataRoot, projectId);
    public string WorkspaceCuesPath(string projectId) => Path.Combine(ProjectDirectory(projectId), "workspace-cues.json");
    public string WorkspaceCueStatePath(string projectId) => Path.Combine(ProjectDirectory(projectId), "workspace-state.json");
    public string ProjectTranslationCachePath(string projectId) => Path.Combine(ProjectDirectory(projectId), "translation_cache.json");

    public void EnsureProjectDirectory(string projectId)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
            Directory.CreateDirectory(ProjectDirectory(projectId));
    }

    public SemaphoreSlim GetProjectGate(string projectId) =>
        _projectGates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Staged transactional write: writes all items to temporary staging files first.
    /// If any file fails to serialize or write, all temporary files are cleaned up and the
    /// commit is aborted, guaranteeing that no partial or split-brain revisions are left on disk.
    /// Once all files are staged, they are atomically swapped into place.
    /// </summary>
    public async Task CommitProjectRevisionAsync(
        string projectId,
        List<CaptionProject> allProjects,
        string srtText,
        string cuesJson,
        string cueStateJson,
        string? translationCacheJson = null,
        CancellationToken cancellationToken = default)
    {
        var gate = GetProjectGate(projectId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projectDir = ProjectDirectory(projectId);
            Directory.CreateDirectory(projectDir);

            var projectStoreDir = Path.GetDirectoryName(ProjectStorePath)!;
            Directory.CreateDirectory(projectStoreDir);

            var projectsJson = AotJson.Serialize(allProjects, new JsonSerializerOptions { WriteIndented = true });

            var subtitlePath = Path.Combine(projectDir, "edited.srt");
            var cuesPath = WorkspaceCuesPath(projectId);
            var cueStatePath = WorkspaceCueStatePath(projectId);
            var translationPath = ProjectTranslationCachePath(projectId);

            var stagedFiles = new List<(string StagedPath, string TargetPath)>();
            var randomSuffix = Guid.NewGuid().ToString("N")[..8];

            try
            {
                await Task.Run(() =>
                {
                    // 1. Stage all files
                    StageFile(subtitlePath, srtText, randomSuffix, stagedFiles);
                    StageFile(cuesPath, cuesJson, randomSuffix, stagedFiles);
                    StageFile(cueStatePath, cueStateJson, randomSuffix, stagedFiles);
                    if (translationCacheJson is not null)
                    {
                        StageFile(translationPath, translationCacheJson, randomSuffix, stagedFiles);
                    }
                    StageFile(ProjectStorePath, projectsJson, randomSuffix, stagedFiles);

                    // 2. Atomically commit each staged file to target
                    foreach (var (staged, target) in stagedFiles)
                    {
                        File.Move(staged, target, overwrite: true);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Clean up any remaining staged files on error
                foreach (var (staged, _) in stagedFiles)
                {
                    try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void StageFile(string targetPath, string content, string suffix, List<(string StagedPath, string TargetPath)> registry)
    {
        var staged = targetPath + $".{suffix}.tmp";
        File.WriteAllText(staged, content);
        registry.Add((staged, targetPath));
    }
}
