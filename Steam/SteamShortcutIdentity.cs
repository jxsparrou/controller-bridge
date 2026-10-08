using System.Text;

namespace SBridge.Steam;

internal static class SteamShortcutIdentity
{
    // Preserve the legacy contract: callers supply an unquoted bridge path.
    // Do not normalize paths or remove quotes without Steam identity fixtures.
    public static uint CalculateAppId(string appName, string exePath)
    {
        string combined = "\"" + exePath + "\"" + appName;
        byte[] bytes = Encoding.UTF8.GetBytes(combined);
        return ComputeCRC32(bytes) | 0x80000000;
    }

    private static uint ComputeCRC32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        uint poly = 0xEDB88320; // Reversed IEEE polynomial
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }
        }
        return ~crc;
    }
}
