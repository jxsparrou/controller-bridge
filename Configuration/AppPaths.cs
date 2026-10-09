using System;
using System.IO;
using System.Runtime.Versioning;

namespace SBridge.Configuration;

internal sealed record AppPaths(string DataDirectory, string ConfigFile, string LogFile, string LegacyConfigFile)
{
    [SupportedOSPlatform("windows")]
    public static AppPaths ForWindows()
    {
        string? testDirectory = Environment.GetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY");
        string data = string.IsNullOrWhiteSpace(testDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sBridge") : testDirectory;
        if (!Path.IsPathFullyQualified(data)) throw new InvalidOperationException("The data directory must be an absolute Windows path.");
        data = Path.GetFullPath(data);
        return new AppPaths(data, Path.Combine(data, "config.json"), Path.Combine(data, "logs", "sBridge.log"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sBridge.cfg"));
    }
}
