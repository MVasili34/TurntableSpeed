using Microsoft.Extensions.Logging;
using TurntableSpeed.App.Services;
using TurntableSpeed.App.ViewModels;
using TurntableSpeed.App.Views;

namespace TurntableSpeed.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if ANDROID
        builder.Services.AddSingleton<ICaptureEnvironment, Capture.AndroidCaptureEnvironment>();
#else
        // Everything degrades into an explanation rather than a crash. This is also what the
        // desktop target used for compile-checking the shared code binds to.
        builder.Services.AddSingleton<ICaptureEnvironment, UnavailableCaptureEnvironment>();
#endif

        // One recorder shared by both modes: the diagnostics screen exports whichever raw data
        // the last measurement produced (§6).
        builder.Services.AddSingleton<SessionRecorder>();

        // Singletons, not transients: a measurement has to survive tab switching.
        builder.Services.AddSingleton<SensorModeViewModel>();
        builder.Services.AddSingleton<AudioModeViewModel>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();

        builder.Services.AddSingleton<SensorModePage>();
        builder.Services.AddSingleton<AudioModePage>();
        builder.Services.AddSingleton<DiagnosticsPage>();

        builder.Services.AddSingleton(_ => Application.Current?.Dispatcher
                                           ?? throw new InvalidOperationException("No dispatcher available."));

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
