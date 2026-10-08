using System.Text.Json;
using System.Diagnostics;

namespace SBridge.Tests;

// An explicit test-only entrypoint. VSTest loads this assembly without running
// Main; Windows integration tests launch its apphost to observe actual argv.
internal static class ArgumentProbe
{
    public static int Main(string[] arguments)
    {
        int sisrProbe = Array.IndexOf(arguments, "--sisr-api-probe");
        if (sisrProbe >= 0 && arguments.Length > sisrProbe + 1)
            return RunSisrProbe(arguments, arguments[sisrProbe + 1]);
        if (arguments.Length == 6 && arguments[0] == "--protocol-chain")
        {
            File.WriteAllText(arguments[1], JsonSerializer.Serialize(new[] { arguments[5] }));
            File.AppendAllText(arguments[2], Environment.ProcessId + Environment.NewLine);
            var next = new ProcessStartInfo(arguments[3]) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string token in new[] { "--session-chain", arguments[2], "700", "400", "2000", arguments[4] }) next.ArgumentList.Add(token);
            using var child = Process.Start(next);
            Thread.Sleep(300);
            return 0;
        }
        if (arguments.Length >= 5 && arguments[0] == "--session-chain")
        {
            string events = arguments[1];
            int beforeSpawn = int.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
            int overlap = int.Parse(arguments[3], System.Globalization.CultureInfo.InvariantCulture);
            int finalLifetime = int.Parse(arguments[4], System.Globalization.CultureInfo.InvariantCulture);
            File.AppendAllText(events, Environment.ProcessId + Environment.NewLine);
            if (arguments.Length > 5)
            {
                Thread.Sleep(beforeSpawn);
                var next = new ProcessStartInfo(arguments[5]) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string token in arguments.Take(5).Concat(arguments.Skip(6))) next.ArgumentList.Add(token);
                using var child = Process.Start(next);
                Thread.Sleep(overlap);
            }
            else Thread.Sleep(finalLifetime);
            return 0;
        }
        if (arguments.Length < 2 || arguments[0] != "--write-arguments") return 2;
        File.WriteAllText(arguments[1], JsonSerializer.Serialize(arguments.Skip(2).ToArray()));
        return 0;
    }

    private static int RunSisrProbe(string[] arguments, string capture)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        File.WriteAllText(capture, JsonSerializer.Serialize(new { pid = Environment.ProcessId, cwd = Environment.CurrentDirectory, arguments }));
        bool unsupported = arguments.Contains("--unsupported-api");
        while (true)
        {
            using var client = listener.AcceptTcpClient(); using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            string? line = reader.ReadLine(); if (line == null) continue;
            while (!string.IsNullOrEmpty(reader.ReadLine())) { }
            bool quit = line.StartsWith("POST /api/v1/quit ", StringComparison.Ordinal);
            string body = quit ? "" : line.Split(' ')[1] switch
            {
                "/api/v1/version/info" => "{\"version\":\"" + (unsupported ? "v0.7.0" : "v0.6.1") + "\"}",
                "/api/v1/steam/status" => "{\"steam_running\":false,\"no_steam_mode\":true,\"launched_via_steam\":false,\"cef_debug_reachable\":false,\"marker_shortcut_present\":false}",
                "/api/v1/viiper/status" => "{\"status\":null}", "/api/v1/devices" => "null",
                _ => "{\"controllerEmulation\":{\"DefaultControllerType\":\"xbox360\"},\"runMisc\":{\"InitialLaunch\":false},\"window\":{\"Fullscreen\":false,\"Show\":false}}"
            };
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
            stream.Write(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 " + (quit ? "204 No Content" : "200 OK") + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"));
            stream.Write(bytes); if (quit) return 0;
        }
    }
}
