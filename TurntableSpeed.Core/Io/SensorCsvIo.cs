using System.Globalization;
using System.Text;
using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.Core.Io;

/// <summary>
/// CSV for tri-axial sensor recordings: <c>t,x,y,z</c> with a header line. Used by the
/// diagnostics screen's raw export and by the fixtures the golden tests replay.
/// </summary>
/// <remarks>
/// Round-trip fidelity matters more than file size here — a fixture that does not read back
/// bit-for-bit would make a golden test lie about a regression. Values are written with the
/// round-trip format specifier.
/// </remarks>
public static class SensorCsvIo
{
    public const string Header = "t,x,y,z";

    public static void Write(TextWriter writer, IEnumerable<Vector3Sample> samples, string? comment = null)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        if (!string.IsNullOrEmpty(comment))
        {
            foreach (var line in comment!.Split('\n'))
            {
                writer.Write('#');
                writer.WriteLine(line.TrimEnd('\r'));
            }
        }

        writer.WriteLine(Header);

        foreach (var sample in samples)
        {
            writer.Write(sample.T.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(sample.X.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(sample.Y.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(sample.Z.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public static void WriteFile(string path, IEnumerable<Vector3Sample> samples, string? comment = null)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        Write(writer, samples, comment);
    }

    public static string WriteString(IEnumerable<Vector3Sample> samples, string? comment = null)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, samples, comment);
        return writer.ToString();
    }

    /// <summary>
    /// Read a sensor CSV. Blank lines and <c>#</c> comments are skipped; a header line is
    /// detected and ignored. Malformed rows throw rather than being silently dropped.
    /// </summary>
    public static List<Vector3Sample> Read(TextReader reader)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        var samples = new List<Vector3Sample>();
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var fields = trimmed.Split(',');
            if (fields.Length < 4)
            {
                throw new InvalidDataException($"Line {lineNumber}: expected 4 fields, found {fields.Length}.");
            }

            if (!TryParse(fields[0], out var t))
            {
                // The header, or any other non-numeric first field.
                if (lineNumber <= 8)
                {
                    continue;
                }

                throw new InvalidDataException($"Line {lineNumber}: '{fields[0]}' is not a number.");
            }

            if (!TryParse(fields[1], out var x) ||
                !TryParse(fields[2], out var y) ||
                !TryParse(fields[3], out var z))
            {
                throw new InvalidDataException($"Line {lineNumber}: malformed sample.");
            }

            samples.Add(new Vector3Sample(t, x, y, z));
        }

        return samples;
    }

    public static List<Vector3Sample> ReadFile(string path)
    {
        using var reader = new StreamReader(path);
        return Read(reader);
    }

    public static List<Vector3Sample> ReadString(string csv) =>
        Read(new StringReader(csv));

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
