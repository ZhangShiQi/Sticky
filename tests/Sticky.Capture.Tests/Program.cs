using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Sticky.Capture;

internal sealed class CaptureTestApplication : Application
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new CaptureTestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        CaptureEnvironment.Configure(Path.Combine(Path.GetTempPath(), "Sticky.Capture.Tests", Guid.NewGuid().ToString("N")));
        app.Startup += async (_, _) =>
        {
            try { app.Shutdown(await RunAsync(args)); }
            catch (Exception ex) { Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/capture-runner-error.txt", ex.ToString()); app.Shutdown(1); }
        };
        return app.Run();
    }
    public static Task CheckCaptureGuardAsync()
    {
        if (!CaptureEnvironment.IsEditingCredentials) throw new InvalidOperationException("Credential editor was not guarded.");
        using var capture = new CaptureCoordinator(CaptureEnvironment.DataDirectory, () => true);
        return capture.StartCaptureAsync();
    }
    private static Task<int> RunAsync(string[] args) => args.FirstOrDefault() switch
    {
        "--self-test" => Task.FromResult(SelfTests.Run()),
        "--smoke-test" => SelfTests.SmokeAsync(),
        "--qr-test" => QrTests.RunAsync(),
        "--window-test" => WindowSelectionTests.RunAsync(),
        "--ocr-test" => OcrTests.RunAsync(),
        "--translation-test" => TranslationTests.RunAsync(),
        "--ai-test" => AiTests.RunAsync(),
        "--vision-test" => VisionTests.RunAsync(),
        "--annotation-test" => AnnotationTests.RunAsync(),
        "--scroll-test" => ScrollTests.RunAsync(),
        "--scroll-native-test" => ScrollTests.NativeAsync(),
        "--auto-scroll-test" => AutoScrollTests.RunAsync(),
        "--auto-scroll-native-test" => AutoScrollTests.NativeAsync(),
        "--scroll-optimization-test" => ScrollOptimizationTests.RunAsync(),
        "--scroll-permissive-test" => ScrollPermissiveTests.RunAsync(),
        "--scroll-web-test" => ScrollWebTests.RunAsync(),
        "--scroll-benchmark" => ScrollOptimizationTests.BenchmarkAsync(),
        "--recording-test" => RecordingTests.RunAsync(),
        "--recording-native-test" => RecordingTests.NativeAsync(),
        "--recording-ui-native-test" => RecordingTests.UiNativeAsync(),
        "--recording-focus-fixture" => RecordingTests.FocusFixtureAsync(),
        _ => throw new ArgumentException("Specify a capture test suite.")
    };
}
