namespace DisplayBridge.Core.Settings;

/// <summary>
/// Maps the wire-level SETTING_REQUEST/CONFIG_UPDATE `key(u8)` byte to a
/// <see cref="SettingField"/> and back. schema.yaml's SettingRequest.key
/// field is documented as "SettingKey enum id" but the concrete enum was
/// never added to schema.yaml in session 2 (M3/M4 landed the classifier
/// and store before the wire-level key numbering was settled) — this is
/// the missing piece, defined here as the PC-side source of truth until
/// it's promoted into schema.yaml + codegen for both sides.
/// IMPORTANT: Android's SETTING_REQUEST sender (floating button, M4.3)
/// MUST use these exact byte values, or requests will silently no-op via
/// the default case in FromWireKey.
/// </summary>
public static class SettingKeyMap
{
    public static byte ToWireKey(SettingField field) => (byte)field;

    public static bool TryFromWireKey(byte key, out SettingField field)
    {
        if (Enum.IsDefined(typeof(SettingField), (int)key))
        {
            field = (SettingField)key;
            return true;
        }

        field = default;
        return false;
    }
}
