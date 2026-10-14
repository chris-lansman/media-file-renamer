using System.IO;
using System.Text.Json;
using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.App.Services;

internal sealed class WorkspaceSession
{
    public int Version { get; set; } = 1;
    public double Width { get; set; } = 1360;
    public double Height { get; set; } = 860;
    public bool Maximized { get; set; }
    public int Preset { get; set; }
    public int Operation { get; set; }
    public string OutputFolder { get; set; } = "";
    public string CustomFormat { get; set; } = "";
    public string SelectedSource { get; set; } = "";
    public List<double> ColumnWidths { get; set; } = [];
    public List<MediaPreviewItem> Items { get; set; } = [];
}

internal sealed class WorkspaceSessionService(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };

    public WorkspaceSession? Load()
    {
        if (!File.Exists(path)) return null;
        // Bound reads of malformed or externally edited session files.
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The saved workspace is too large to restore.");
        var session = JsonSerializer.Deserialize<WorkspaceSession>(File.ReadAllText(path), Options);
        if (session is null || session.Version != 1 || session.Items is null || session.ColumnWidths is null
            || session.Items.Count > 10000 || session.Items.Any(item => item is null || !Path.IsPathFullyQualified(item.SourcePath)))
            throw new InvalidDataException("The saved workspace is not a supported review session.");
        return session;
    }

    public void Save(WorkspaceSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(session, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void RestoreDetails(MediaPreviewItem saved, MediaPreviewItem scanned)
    {
        scanned.MatchedTitle = saved.MatchedTitle;
        scanned.MediaType = saved.MediaType;
        scanned.Year = saved.Year;
        scanned.Season = saved.Season;
        scanned.Episode = saved.Episode;
        scanned.EpisodeEnd = saved.EpisodeEnd;
        scanned.EpisodeTitle = saved.EpisodeTitle;
        scanned.TmdbId = saved.TmdbId;
        scanned.TvdbId = saved.TvdbId;
        scanned.EpisodeOrder = saved.EpisodeOrder;
        scanned.AirDate = saved.AirDate;
        scanned.AbsoluteEpisode = saved.AbsoluteEpisode;
        scanned.PartNumber = saved.PartNumber;
        scanned.Edition = saved.Edition;
        // Destination and companions always come from the current scan/planner.
        // Never trust a saved status as permission to modify files after restart.
        scanned.Status = "Restored session needs review: confirm details or retry matching.";
    }
}
