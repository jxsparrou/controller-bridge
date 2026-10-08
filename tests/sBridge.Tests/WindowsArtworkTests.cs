using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using SBridge.Configuration;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

[SupportedOSPlatform("windows")]
[Collection("Windows integration")]
public class WindowsArtworkTests
{
    private static Assembly? application;
    private static Assembly ApplicationAssembly()
    {
        if (application != null) return application;
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        string runtime = Directory.GetDirectories(desktop).Where(path => Path.GetFileName(path).StartsWith("10.", StringComparison.Ordinal) && !Path.GetFileName(path).Contains('-'))
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path))).First();
        // Resolve the application's installed Windows Desktop assemblies only for
        // native image/UI checks. Portable tests still link core sources and do
        // not need a Windows Desktop target or additional packages.
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(runtime, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        string app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../bin/Release/net10.0-windows/sBridge.dll"));
        application = AssemblyLoadContext.Default.LoadFromAssemblyPath(app);
        return application;
    }

    [WindowsFact]
    public void ProductionImageDecoderValidatesPngRejectsCorruptPayloadAndConvertsJpegIcon()
    {
        var assembly = ApplicationAssembly();
        var process = assembly.GetType("SBridge.Artwork.WindowsArtworkImages")!.GetMethod("Process")!;
        byte[] png = ArtworkTests.Png();
        var image = process.Invoke(null, [png, true])!;
        byte[] converted = (byte[])image.GetType().GetProperty("Bytes")!.GetValue(image)!;
        Assert.Equal(".png", SBridge.Artwork.ArtworkImageLimits.Inspect(converted));
        Assert.NotEmpty(converted);
        byte[] corrupt = png[..33]; // Valid dimensions/header, missing compressed pixels.
        Assert.IsType<InvalidDataException>(Assert.Throws<TargetInvocationException>(() => process.Invoke(null, [corrupt, false])).InnerException);
        var drawing = Assembly.Load("System.Drawing.Common");
        var nativeImageType = drawing.GetType("System.Drawing.Image")!;
        using var stream = new MemoryStream(png);
        using var nativeImage = (IDisposable)nativeImageType.GetMethod("FromStream", [typeof(Stream)])!.Invoke(null, [stream])!;
        var formatType = drawing.GetType("System.Drawing.Imaging.ImageFormat")!;
        var jpegFormat = formatType.GetProperty("Jpeg")!.GetValue(null);
        using var output = new MemoryStream();
        nativeImageType.GetMethod("Save", [typeof(Stream), formatType])!.Invoke(nativeImage, [output, jpegFormat]);
        byte[] jpeg = output.ToArray(); Assert.Equal(".jpg", SBridge.Artwork.ArtworkImageLimits.Inspect(jpeg));
        image = process.Invoke(null, [jpeg, true])!;
        Assert.Equal(".png", SBridge.Artwork.ArtworkImageLimits.Inspect((byte[])image.GetType().GetProperty("Bytes")!.GetValue(image)!));
    }

    [ArtworkUiFact]
    public void ActualSettingsQueueBoundsConcurrencyAndDefersCloseUntilCancelledJobsFinish()
    {
        var app = ApplicationAssembly();
        string directory = Path.Combine(Path.GetTempPath(), "sBridge-artwork-ui-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(directory, "Data"); string steam = Path.Combine(directory, "Steam");
        Directory.CreateDirectory(data); Directory.CreateDirectory(Path.Combine(steam, "userdata", "1"));
        var settings = new AppSettings { SisrEnabled = false, SteamGridDbApiKey = "native-test-key" }; settings.SelectedSteamAccountIds.Add("1");
        File.WriteAllBytes(Path.Combine(data, "config.json"), JsonSettingsCodec.Encode(settings, null, new WindowsSecretProtector()));
        string? previousData = Environment.GetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY");
        string? previousSteam = Environment.GetEnvironmentVariable("SBRIDGE_TEST_STEAM_DIRECTORY");
        int active = 0, peak = 0, sent = 0; Exception? failure = null;
        using var client = new HttpClient(new ArtworkTests.Handler(async (_, token) =>
        {
            Interlocked.Increment(ref sent); int current = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref peak, Math.Max(Volatile.Read(ref peak), current));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception("unreachable"); }
            finally { Interlocked.Decrement(ref active); }
        }));
        try
        {
            Environment.SetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY", data); Environment.SetEnvironmentVariable("SBRIDGE_TEST_STEAM_DIRECTORY", steam);
            var thread = new Thread(() =>
            {
                object? form = null;
                try
                {
                    app.GetType("Program")!.GetMethod("LoadConfig")!.Invoke(null, null);
                    var httpType = app.GetType("SBridge.Artwork.ArtworkHttpClient")!;
                    var http = Activator.CreateInstance(httpType, [client, null, null]);
                    var imageType = app.GetType("SBridge.Artwork.ArtworkImage")!;
                    var imageProcessor = app.GetType("SBridge.Artwork.WindowsArtworkImages")!.GetMethod("Process")!
                        .CreateDelegate(typeof(Func<,,>).MakeGenericType(typeof(byte[]), typeof(bool), imageType));
                    var service = Activator.CreateInstance(app.GetType("SBridge.Artwork.ArtworkService")!, [http, imageProcessor, null, TimeSpan.FromSeconds(10)]);
                    var formType = app.GetType("Program+SettingsForm")!;
                    form = Activator.CreateInstance(formType, BindingFlags.Instance | BindingFlags.NonPublic, null, [service], null)!;
                    var account = Activator.CreateInstance(app.GetType("SBridge.Steam.SteamAccount")!, [steam, "1", "Test account"]);
                    var request = Activator.CreateInstance(app.GetType("SBridge.Artwork.ArtworkRequest")!, [account, "Test game", 0xF1234567u, "exe", "launch test", ""]);
                    var timerType = Assembly.Load("System.Windows.Forms").GetType("System.Windows.Forms.Timer")!;
                    using var timer = (IDisposable)Activator.CreateInstance(timerType)!;
                    timerType.GetProperty("Interval")!.SetValue(timer, 300);
                    timerType.GetEvent("Tick")!.AddEventHandler(timer, new EventHandler((_, _) =>
                    {
                        timerType.GetMethod("Stop")!.Invoke(timer, null); formType.GetMethod("Close")!.Invoke(form, null);
                    }));
                    formType.GetEvent("Shown")!.AddEventHandler(form, new EventHandler((_, _) =>
                    {
                        for (int index = 0; index < 33; index++) formType.GetMethod("QueueArtwork", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [request]);
                        Assert.Equal(32, formType.GetField("artworkPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
                        timerType.GetMethod("Start")!.Invoke(timer, null);
                    }));
                    var applicationType = Assembly.Load("System.Windows.Forms").GetType("System.Windows.Forms.Application")!;
                    applicationType.GetMethods().Single(method => method.Name == "Run" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.FullName == "System.Windows.Forms.Form")
                        .Invoke(null, [form]);
                    Assert.Equal(0, formType.GetField("artworkPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
                    Assert.Equal(1, formType.GetField("artworkWarnings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
                }
                catch (Exception ex) { failure = ex; }
                finally { (form as IDisposable)?.Dispose(); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Native settings cancellation did not finish.");
            Assert.Null(failure); Assert.Equal(2, peak); Assert.Equal(2, sent); Assert.Equal(0, active);
            Assert.False(Directory.Exists(Path.Combine(steam, "userdata", "1", "config")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SBRIDGE_TEST_DATA_DIRECTORY", previousData); Environment.SetEnvironmentVariable("SBRIDGE_TEST_STEAM_DIRECTORY", previousSteam);
            Directory.Delete(directory, true);
        }
    }
}

internal sealed class ArtworkUiFactAttribute : FactAttribute
{
    public ArtworkUiFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows WinForms/image validation.";
        else if (Environment.GetEnvironmentVariable("SBRIDGE_TEST_ARTWORK_UI") != "1") Skip = "Opt-in: set SBRIDGE_TEST_ARTWORK_UI=1 for isolated artwork queue/close UI verification.";
    }
}
