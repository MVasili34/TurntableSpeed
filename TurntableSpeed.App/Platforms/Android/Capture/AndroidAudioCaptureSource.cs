using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Android.Content;
using Android.Media;
using Android.Media.Audiofx;
using TurntableSpeed.App.Localization;
using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.App.Capture;

/// <summary>
/// Microphone capture, and the part of the app §4.1 calls a precondition rather than a detail:
/// noise suppression, automatic gain control and echo cancellation destroy exactly the
/// transients the click estimator lives on, so each is asked for by name and the answer is
/// recorded in <see cref="Report"/> instead of being assumed.
/// </summary>
public sealed class AndroidAudioCaptureSource : IAudioCaptureSource
{
    private readonly Context _context;

    public AndroidAudioCaptureSource(Context context, AudioCaptureSettings settings)
    {
        _context = context;
        Settings = settings;
    }

    public AudioCaptureSettings Settings { get; }

    public AudioCaptureReport Report { get; private set; } = AudioCaptureReport.Unknown;

    public async IAsyncEnumerable<AudioBlock> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var unprocessedSupported = IsUnprocessedSupported();
        var source = Settings.PreferUnprocessed && unprocessedSupported
            ? AudioSource.Unprocessed
            : AudioSource.Mic;

        var minimum = AudioRecord.GetMinBufferSize(Settings.SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        var bufferSize = Math.Max(minimum, Settings.BlockSize * sizeof(short) * 4);

        using var record = new AudioRecord(
            source, Settings.SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, bufferSize);

        if (record.State != State.Initialized)
        {
            throw new InvalidOperationException(AppStrings.ErrorAudioRecordInit);
        }

        var effects = DisableProcessing(record.AudioSessionId);

        Report = new AudioCaptureReport(
            Settings.SampleRate,
            UnprocessedSourceRequested: Settings.PreferUnprocessed,
            UnprocessedSourceGranted: source == AudioSource.Unprocessed,
            NoiseSuppressorDisabled: effects.NoiseSuppressor,
            AutomaticGainControlDisabled: effects.AutomaticGainControl,
            AcousticEchoCancelerDisabled: effects.EchoCanceler,
            Details: unprocessedSupported
                ? Tr.Format(AppStrings.AndroidAudioDetails, source, Settings.SampleRate, bufferSize)
                : Tr.Format(AppStrings.AndroidAudioUnprocessedUnsupported, source, Settings.SampleRate));

        var channel = Channel.CreateBounded<AudioBlock>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        record.StartRecording();

        // AudioRecord.Read blocks. A dedicated thread keeps that away from the consumer, and
        // gives the read loop a predictable cadence.
        var reader = new Thread(() => ReadLoop(record, channel.Writer, ct))
        {
            IsBackground = true,
            Name = "turntable-audio",
            Priority = ThreadPriority.AboveNormal,
        };

        reader.Start();

        try
        {
            await foreach (var block in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return block;
            }
        }
        finally
        {
            channel.Writer.TryComplete();

            try
            {
                record.Stop();
            }
            catch (Java.Lang.IllegalStateException)
            {
                // Already stopped; nothing to do.
            }

            foreach (var effect in effects.Instances)
            {
                effect.Release();
                effect.Dispose();
            }
        }
    }

    private void ReadLoop(AudioRecord record, ChannelWriter<AudioBlock> writer, CancellationToken ct)
    {
        var buffer = new short[Settings.BlockSize];
        long total = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = record.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    // Negative values are error codes; zero means the device gave us nothing.
                    break;
                }

                var samples = new float[read];
                for (var i = 0; i < read; i++)
                {
                    samples[i] = buffer[i] / 32768f;
                }

                // Timestamps from the sample counter, never from a clock: the core measures a
                // frequency, and a wall clock would inject jitter into the time base itself.
                var time = total / (double)Settings.SampleRate;
                total += read;

                writer.TryWrite(new AudioBlock(time, samples, Settings.SampleRate));
            }
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            return;
        }

        writer.TryComplete();
    }

    private bool IsUnprocessedSupported()
    {
        // PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED, as §4.1 asks. A device that does not
        // advertise it may still accept the source, but the honest thing is to report what it
        // claimed rather than what we hoped.
        if (_context.GetSystemService(Context.AudioService) is not AudioManager manager)
        {
            return false;
        }

        var property = manager.GetProperty(AudioManager.PropertySupportAudioSourceUnprocessed);
        return string.Equals(property, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turn every processing block off explicitly. Each returns whether it is now known to be
    /// off — "not available on this device" also counts, because then it was never applied.
    /// </summary>
    private static (bool NoiseSuppressor, bool AutomaticGainControl, bool EchoCanceler, List<AudioEffect> Instances)
        DisableProcessing(int sessionId)
    {
        var instances = new List<AudioEffect>(3);

        var noise = !NoiseSuppressor.IsAvailable || Disable(NoiseSuppressor.Create(sessionId), instances);
        var gain = !AutomaticGainControl.IsAvailable || Disable(AutomaticGainControl.Create(sessionId), instances);
        var echo = !AcousticEchoCanceler.IsAvailable || Disable(AcousticEchoCanceler.Create(sessionId), instances);

        return (noise, gain, echo, instances);

        static bool Disable(AudioEffect? effect, List<AudioEffect> instances)
        {
            if (effect is null)
            {
                return false;
            }

            instances.Add(effect);

            try
            {
                effect.SetEnabled(false);
                return !effect.Enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
