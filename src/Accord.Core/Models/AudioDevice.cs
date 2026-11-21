namespace Accord.Core.Models;

public sealed class AudioDevice
{
    public const int StateActive = 1;
    public const int StateDisabled = 2;
    public const int StateNotPresent = 4;
    public const int StateUnplugged = 8;

    public string Flow { get; set; } = "Render"; // "Render" | "Capture"
    public string Id { get; set; } = string.Empty; // "{guid}"
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int State { get; set; }
    public string HardwarePath { get; set; } = string.Empty;
    public string WaveId { get; set; } = string.Empty;
    public bool IsDefaultMultimedia { get; set; }
    public bool IsDefaultCommunications { get; set; }
    public bool HasBackup { get; set; }

    public string FlowLabel => Flow == "Render" ? "Lecture" : "Enregistrement";

    public string EndpointId =>
        (Flow == "Render" ? "{0.0.0.00000000}." : "{0.0.1.00000000}.") + Id;

    public string FullDisplayName =>
        string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} ({Description})";

    public string StateLabel => State switch
    {
        StateActive => "Actif",
        StateDisabled => "Désactivé",
        StateUnplugged => "Déconnecté",
        StateNotPresent => "Absent",
        _ => "Inconnu"
    };

    public bool IsActive => State == StateActive;
    public bool IsDisabled => State == StateDisabled;
    public bool IsDisconnected => State == StateUnplugged;
    public bool IsHidden => State == StateNotPresent || State == 0;

    public bool IsHandsFree =>
        Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
        Description.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);

    public bool IsDefault => IsDefaultMultimedia || IsDefaultCommunications;
}
