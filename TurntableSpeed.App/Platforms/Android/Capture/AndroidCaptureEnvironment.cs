using Android.Content;
using TurntableSpeed.App.Localization;
using TurntableSpeed.App.Services;
using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.App.Capture;

/// <summary>The Android side of <see cref="ICaptureEnvironment"/>.</summary>
public sealed class AndroidCaptureEnvironment : ICaptureEnvironment
{
    private readonly Context _context;
    private readonly AndroidSensorSuite _sensors;

    public AndroidCaptureEnvironment()
    {
        _context = Android.App.Application.Context;
        _sensors = new AndroidSensorSuite(_context);
    }

    public ISensorSuite Sensors => _sensors;

    public IPermissionBroker Permissions { get; } = new AndroidPermissionBroker();

    public string Description =>
        $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model}, " +
        $"Android {Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt}); " +
        _sensors.Describe();

    public IAudioCaptureSource CreateAudioCapture(AudioCaptureSettings settings) =>
        new AndroidAudioCaptureSource(_context, settings);

    public Task<string> ExportAsync(string fileName, Action<Stream> write, CancellationToken ct = default)
    {
        // App-specific external storage: visible over USB and in a file manager, and needs no
        // permission on any supported version. Good enough for handing a recording to the
        // fixture set, which is what §6 wants the export for.
        var directory = _context.GetExternalFilesDir(null)?.AbsolutePath
                        ?? _context.FilesDir?.AbsolutePath
                        ?? throw new IOException(AppStrings.ErrorNoExportDirectory);

        Directory.CreateDirectory(directory);

        var stamped = $"{Path.GetFileNameWithoutExtension(fileName)}-{DateTime.Now:yyyyMMdd-HHmmss}" +
                      Path.GetExtension(fileName);
        var path = Path.Combine(directory, stamped);

        using (var stream = File.Create(path))
        {
            write(stream);
        }

        return Task.FromResult(path);
    }
}

/// <summary>Microphone permission, through MAUI's own broker rather than a raw Android call.</summary>
public sealed class AndroidPermissionBroker : IPermissionBroker
{
    public async Task<bool> RequestMicrophoneAsync(CancellationToken ct = default)
    {
        var status = await Permissions
            .CheckStatusAsync<Permissions.Microphone>()
            .ConfigureAwait(false);

        if (status != PermissionStatus.Granted)
        {
            status = await Permissions
                .RequestAsync<Permissions.Microphone>()
                .ConfigureAwait(false);
        }

        return status == PermissionStatus.Granted;
    }

    public async Task<bool> IsMicrophoneGrantedAsync(CancellationToken ct = default)
    {
        var status = await Permissions
            .CheckStatusAsync<Permissions.Microphone>()
            .ConfigureAwait(false);

        return status == PermissionStatus.Granted;
    }
}
