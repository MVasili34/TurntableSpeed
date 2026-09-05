using System.Text;

namespace TurntableSpeed.Core.Io;

/// <summary>A decoded mono WAV file: the samples, normalised to ±1, and their rate.</summary>
public sealed record WavData(float[] Samples, int SampleRate, int SourceChannels, int SourceBitsPerSample)
{
    public double Duration => SampleRate > 0 ? Samples.Length / (double)SampleRate : 0.0;
}

/// <summary>
/// Minimal RIFF/WAVE reader and writer. Enough for fixtures and for the diagnostics screen's
/// raw export — deliberately not a general-purpose audio library.
/// </summary>
public static class WavIo
{
    private const int FormatPcm = 1;
    private const int FormatIeeeFloat = 3;
    private const int FormatExtensible = 0xFFFE;

    /// <summary>
    /// Read a WAV file and downmix to mono. Supports 8/16/24/32-bit PCM and 32/64-bit float,
    /// including WAVE_FORMAT_EXTENSIBLE.
    /// </summary>
    public static WavData Read(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (ReadFourCc(reader) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadUInt32();

        if (ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("RIFF file is not WAVE.");
        }

        var format = 0;
        var channels = 0;
        var sampleRate = 0;
        var bitsPerSample = 0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var id = ReadFourCc(reader);
            var size = reader.ReadUInt32();
            var next = stream.Position + size + (size % 2);

            switch (id)
            {
                case "fmt ":
                    format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadInt32();
                    reader.ReadInt32();  // byte rate
                    reader.ReadUInt16(); // block align
                    bitsPerSample = reader.ReadUInt16();

                    if (format == FormatExtensible && size >= 40)
                    {
                        reader.ReadUInt16(); // cbSize
                        reader.ReadUInt16(); // valid bits
                        reader.ReadUInt32(); // channel mask
                        // The sub-format GUID starts with the real format tag.
                        format = reader.ReadUInt16();
                    }

                    break;

                case "data":
                    data = reader.ReadBytes((int)size);
                    break;
            }

            // Always resume from the declared chunk end: unknown chunks are skipped whole, and
            // a truncated file stops the loop rather than reading into the next chunk.
            stream.Position = Math.Min(next, stream.Length);
        }

        if (data is null || channels <= 0 || sampleRate <= 0)
        {
            throw new InvalidDataException("WAVE file has no usable fmt/data chunks.");
        }

        var interleaved = Decode(data, format, bitsPerSample);
        var frames = interleaved.Length / channels;
        var mono = new float[frames];

        for (var i = 0; i < frames; i++)
        {
            var sum = 0.0;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[i * channels + c];
            }

            mono[i] = (float)(sum / channels);
        }

        return new WavData(mono, sampleRate, channels, bitsPerSample);
    }

    public static WavData ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Write mono audio. 16-bit PCM by default; pass 32 for IEEE float.</summary>
    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate, int bitsPerSample = 16)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (bitsPerSample is not (16 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 16-bit PCM and 32-bit float are written.");
        }

        var format = bitsPerSample == 32 ? FormatIeeeFloat : FormatPcm;
        var bytesPerSample = bitsPerSample / 8;
        var dataSize = samples.Length * bytesPerSample;

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        WriteFourCc(writer, "RIFF");
        writer.Write(36 + dataSize);
        WriteFourCc(writer, "WAVE");

        WriteFourCc(writer, "fmt ");
        writer.Write(16);
        writer.Write((ushort)format);
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * bytesPerSample);
        writer.Write((ushort)bytesPerSample);
        writer.Write((ushort)bitsPerSample);

        WriteFourCc(writer, "data");
        writer.Write(dataSize);

        if (bitsPerSample == 32)
        {
            foreach (var sample in samples)
            {
                writer.Write(sample);
            }
        }
        else
        {
            foreach (var sample in samples)
            {
                // Scale by 32768 to match the reader, then clamp: scaling by 32767 instead would
                // make every round trip come back 1/32768 quiet, which is a systematic error
                // rather than the half-a-step quantisation this leaves behind.
                var scaled = Math.Round(Math.Clamp(sample, -1f, 1f) * 32768.0);
                writer.Write((short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
            }
        }
    }

    public static void WriteFile(string path, ReadOnlySpan<float> samples, int sampleRate, int bitsPerSample = 16)
    {
        using var stream = File.Create(path);
        Write(stream, samples, sampleRate, bitsPerSample);
    }

    private static float[] Decode(byte[] data, int format, int bitsPerSample)
    {
        if (format == FormatIeeeFloat)
        {
            return bitsPerSample switch
            {
                32 => DecodeFloat32(data),
                64 => DecodeFloat64(data),
                _ => throw new InvalidDataException($"Unsupported float width {bitsPerSample}."),
            };
        }

        if (format != FormatPcm)
        {
            throw new InvalidDataException($"Unsupported WAVE format tag {format}.");
        }

        return bitsPerSample switch
        {
            8 => DecodePcm8(data),
            16 => DecodePcm16(data),
            24 => DecodePcm24(data),
            32 => DecodePcm32(data),
            _ => throw new InvalidDataException($"Unsupported PCM width {bitsPerSample}."),
        };
    }

    private static float[] DecodePcm8(byte[] data)
    {
        var result = new float[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            // 8-bit WAV is unsigned, centred on 128.
            result[i] = (data[i] - 128) / 128f;
        }

        return result;
    }

    private static float[] DecodePcm16(byte[] data)
    {
        var count = data.Length / 2;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        }

        return result;
    }

    private static float[] DecodePcm24(byte[] data)
    {
        var count = data.Length / 3;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            var offset = i * 3;
            var value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
            if ((value & 0x800000) != 0)
            {
                value |= unchecked((int)0xFF000000);
            }

            result[i] = value / 8388608f;
        }

        return result;
    }

    private static float[] DecodePcm32(byte[] data)
    {
        var count = data.Length / 4;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToInt32(data, i * 4) / 2147483648f;
        }

        return result;
    }

    private static float[] DecodeFloat32(byte[] data)
    {
        var count = data.Length / 4;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToSingle(data, i * 4);
        }

        return result;
    }

    private static float[] DecodeFloat64(byte[] data)
    {
        var count = data.Length / 8;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = (float)BitConverter.ToDouble(data, i * 8);
        }

        return result;
    }

    private static string ReadFourCc(BinaryReader reader) =>
        new(reader.ReadChars(4));

    private static void WriteFourCc(BinaryWriter writer, string id) =>
        writer.Write(id.ToCharArray(), 0, 4);
}
