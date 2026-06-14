// Stage 7.7: WelcomeViewModel. Backs WelcomeWindow (Surface 04 + embedded
// Surface 10 About panel). Loads patch notes from embedded
// patch_notes.json resource (extracted at brief start from
// tray.ps1 PATCH_HISTORY at lines 261-1445; 292 versions).
//
// Notes virtualize via WPF VirtualizingStackPanel (default for ListBox);
// the full set lives in memory but render is bounded to visible rows.

using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.ViewModels;

public sealed partial class WelcomeViewModel : ObservableObject
{
    private const string Component = "Welcome";

    private readonly ILogger _logger;

    [ObservableProperty]
    private ObservableCollection<PatchNoteVersion> patchNotes = new();

    [ObservableProperty]
    private string currentVersion = "v14.0.0-rc.1";

    [ObservableProperty]
    private string buildDate = DateTime.Now.ToString("yyyy.MM.dd");

    [ObservableProperty]
    private bool showAboutTab;

    [ObservableProperty]
    private bool isFirstRun;

    [ObservableProperty]
    private int countdownSeconds = 5;

    [ObservableProperty]
    private bool dontShowAgain;

    [ObservableProperty]
    private bool justUpdated;

    [ObservableProperty]
    private string updatedToVersion = string.Empty;

    private bool _patchNotesLoaded;

    public WelcomeViewModel(ILogger logger)
    {
        _logger = logger;
        TryLoadPatchNotes();
    }

    public void TryLoadPatchNotes()
    {
        if (_patchNotesLoaded) return;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Embedded resource name: <RootNamespace>.<filename>
            var resourceName = "MastersFM.Tray.patch_notes.json";
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger.LogWarn("patch_notes.json embedded resource not found; resource list: " +
                    string.Join(",", asm.GetManifestResourceNames()), Component);
                return;
            }
            var entries = JsonSerializer.Deserialize<List<PatchNoteVersionDto>>(stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            if (entries == null) return;
            foreach (var v in entries)
            {
                var version = new PatchNoteVersion
                {
                    Version = v.Version ?? "(unknown)",
                    Date = v.Date ?? "",
                    Notes = new List<PatchNote>()
                };
                if (v.Notes != null)
                {
                    foreach (var n in v.Notes)
                    {
                        version.Notes.Add(new PatchNote
                        {
                            Tag = (n.Tag ?? "NEW").ToUpperInvariant(),
                            Text = n.Text ?? ""
                        });
                    }
                }
                PatchNotes.Add(version);
            }
            _patchNotesLoaded = true;
            _logger.Log($"loaded {PatchNotes.Count} patch versions from embedded resource", Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("loading patch_notes.json", ex, Component);
        }
    }

    [RelayCommand]
    private void Continue()
    {
        // Closing handled by code-behind (DialogResult); VM just signals.
        _logger.Log("Continue clicked (firstRun=" + IsFirstRun + ", dontShowAgain=" + DontShowAgain + ")",
            Component);
    }
}

public sealed class PatchNoteVersion
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public List<PatchNote> Notes { get; set; } = new();
}

public sealed class PatchNote
{
    public string Tag { get; set; } = "";
    public string Text { get; set; } = "";
}

// DTO matching JSON shape (lowercase property names).
internal sealed class PatchNoteVersionDto
{
    public string? Version { get; set; }
    public string? Date { get; set; }
    public List<PatchNoteDto>? Notes { get; set; }
}

internal sealed class PatchNoteDto
{
    public string? Tag { get; set; }
    public string? Text { get; set; }
}
