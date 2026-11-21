namespace Accord.Core.Models;

public sealed class AppSettings
{
    /// <summary>
    /// "Dark", "Light", or "System".
    /// </summary>
    public string Theme { get; set; } = "System";

    public bool ShowActive { get; set; } = true;
    public bool ShowDisabled { get; set; } = false;
    public bool ShowDisconnected { get; set; } = false;

    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Optional explicit endpoint IDs ("{0.0.0.00000000}.{guid}").
    /// Empty string means automatic pre-detection (Pixel Buds / Razer BlackShark / Philips Evnia).
    /// </summary>
    public ProfileTargets Profiles { get; set; } = new();

    /// <summary>
    /// Optional manual profile forced from the UI ("Pixel", "Razer", "Screen") or empty for automatic.
    /// Cleared automatically when a headset connection state changes.
    /// </summary>
    public string ManualProfileOverride { get; set; } = string.Empty;
    public long ManualOverrideStamp { get; set; }
}

public sealed class ProfileTargets
{
    public string PixelPlaybackId { get; set; } = string.Empty;
    public string PixelRecordingId { get; set; } = string.Empty;
    public string RazerPlaybackId { get; set; } = string.Empty;
    public string RazerRecordingId { get; set; } = string.Empty;
    public string ScreenPlaybackId { get; set; } = string.Empty;
}
