namespace DisplayBridge.Core.Settings;

/// <summary>Thrown when a settings value fails validation and cannot be safely clamped (e.g. Custom resolution exceeding device Max).</summary>
public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string message) : base(message)
    {
    }
}
