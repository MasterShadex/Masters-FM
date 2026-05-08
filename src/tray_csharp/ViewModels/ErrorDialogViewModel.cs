// Stage 7.7: ErrorDialogViewModel. Backs ErrorDialogWindow (Surface 11).
// Per Q-MOCK-11a default: info icon + brand purple, NOT destructive red.
// No "Submit feedback" action per Q-MOCK-11b default.

using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.ViewModels;

public sealed partial class ErrorDialogViewModel : ObservableObject
{
    private const string Component = "ErrorDialog";

    private readonly ILogger _logger;

    [ObservableProperty]
    private string title = "Master's FM ran into a problem";

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string technicalDetails = "";

    [ObservableProperty]
    private bool hasTechnicalDetails;

    [ObservableProperty]
    private bool showTechnicalDetails;

    public ErrorDialogViewModel(ILogger logger)
    {
        _logger = logger;
    }

    public void Populate(string title, string message, Exception? ex)
    {
        Title = string.IsNullOrEmpty(title) ? "Master's FM ran into a problem" : title;
        Message = message ?? "";
        if (ex != null)
        {
            TechnicalDetails = ex.ToString();
            HasTechnicalDetails = true;
        }
        else
        {
            TechnicalDetails = "";
            HasTechnicalDetails = false;
        }
        ShowTechnicalDetails = false;
    }

    [RelayCommand]
    private void CopyDetails()
    {
        try
        {
            var combined = Title + "\n\n" + Message;
            if (HasTechnicalDetails) combined += "\n\n--- Technical details ---\n" + TechnicalDetails;
            Clipboard.SetText(combined);
            _logger.Log("error details copied to clipboard", Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("clipboard copy", ex, Component);
        }
    }

    [RelayCommand]
    private void OpenLog()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MastersFM", "overlay.log");
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("open log", ex, Component);
        }
    }

    [RelayCommand]
    private void ToggleTechnicalDetails()
    {
        ShowTechnicalDetails = !ShowTechnicalDetails;
    }
}
