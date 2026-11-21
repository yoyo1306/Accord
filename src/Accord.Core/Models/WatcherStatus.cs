namespace Accord.Core.Models;

public sealed class WatcherStatus
{
    public DateTime UpdatedUtc { get; set; }
    public int WatcherPid { get; set; }
    public string ActiveProfile { get; set; } = "Screen";
    public bool ManualOverride { get; set; }
    public bool PixelConnected { get; set; }
    public bool RazerConnected { get; set; }
    public string RazerStateLabel { get; set; } = "Déconnecté";

    public string PixelPlaybackName { get; set; } = string.Empty;
    public string PixelRecordingName { get; set; } = string.Empty;
    public string RazerPlaybackName { get; set; } = string.Empty;
    public string RazerRecordingName { get; set; } = string.Empty;
    public string ScreenPlaybackName { get; set; } = string.Empty;

    public string DefaultPlaybackName { get; set; } = string.Empty;
    public string DefaultRecordingName { get; set; } = string.Empty;
    public string LastEvent { get; set; } = string.Empty;
}
