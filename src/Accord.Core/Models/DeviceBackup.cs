namespace Accord.Core.Models;

public sealed class DeviceBackup
{
    public string Flow { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool HasAlias { get; set; }
    public string Alias { get; set; } = string.Empty;

    public bool HasInterface { get; set; }
    public string InterfaceName { get; set; } = string.Empty;

    public bool HasFriendly { get; set; }
    public string FriendlyName { get; set; } = string.Empty;

    public bool HasDeviceDesc { get; set; }
    public string DeviceDesc { get; set; } = string.Empty;

    public string Hw { get; set; } = string.Empty;
    public string Wave { get; set; } = string.Empty;

    public string EndpointId =>
        (Flow == "Render" ? "{0.0.0.00000000}." : "{0.0.1.00000000}.") + Id;

    public string SavedDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(FriendlyName)) return FriendlyName;
            if (!string.IsNullOrEmpty(Description)) return $"{Name} ({Description})";
            return Name;
        }
    }

    public string SavedInterfaceLabel
    {
        get
        {
            string display = SavedDisplay;
            string prefix = Name + " (";
            if (!string.IsNullOrEmpty(Name) && display.StartsWith(prefix, StringComparison.Ordinal) && display.EndsWith(")", StringComparison.Ordinal))
            {
                return display.Substring(prefix.Length, display.Length - prefix.Length - 1);
            }

            if (!string.IsNullOrEmpty(Description)) return Description;
            return Name;
        }
    }

    public bool IsHandsFree
    {
        get
        {
            string blob = $"{Name} {Description} {Alias} {FriendlyName} {InterfaceName}";
            return blob.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
        }
    }
}
