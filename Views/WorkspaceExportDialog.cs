using Avalonia.Controls;

namespace AstraCat;

public sealed record WorkspaceExportRequest(
    string SourcePath,
    string ProjectTitle,
    string? SuggestedOutputPath = null,
    string? SubtitlePath = null,
    string? PlainSubtitlePath = null);

public sealed record WorkspaceExportResult(bool Succeeded, bool Cancelled, string? OutputPath, string? ErrorMessage);

/// <summary>Single public entry point for opening the workspace export experience.</summary>
public static class WorkspaceExportDialog
{
    public static Task<WorkspaceExportResult?> ShowAsync(Window owner, WorkspaceExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new MediaExportWindow(request);
        return dialog.ShowDialog<WorkspaceExportResult?>(owner);
    }
}
