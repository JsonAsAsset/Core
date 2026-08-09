using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;

using Serilog;

namespace Core.Resources.Utilities;

public static class AudioUtilities
{
    /* Formats anything downstream can be expected to open on its own */
    private const string Wav = "wav";
    private const string Ogg = "ogg";

    /* Covers the formats without a decoder of their own, when one has been put in the folder */
    private const string VgmStream = "vgmstream-cli.exe";

    /* The ones this build carries a decoder for, and that nothing else reads */
    private static readonly string[] RadFormats = ["rada", "binka"];

    public static DirectoryInfo DecodersFolder => Globals.AudioDecodersFolder;

    private static readonly object DecodersLock = new();
    private static bool ExtractedDecoders;

    /* What the format actually is, as a file extension */
    public static string ExtensionFor(string? audioFormat)
    {
        return audioFormat?.ToUpperInvariant() switch
        {
            "RADA" => "rada",
            "BINKA" => "binka",
            "WEM" => "wem",
            "AT9" => "at9",
            "OPUS" => "opus",
            "ADPCM" => "adpcm",
            "PCM" or "WAV" => Wav,
            "OGG" => Ogg,
            null or "" => Ogg,
            _ => audioFormat.ToLowerInvariant()
        };
    }

    /* Whether the format is already something to hand over as it is */
    public static bool IsReadable(string? audioFormat)
    {
        var extension = ExtensionFor(audioFormat);

        return extension is Wav or Ogg;
    }

    /* Writes out the decoders that ship inside the build, once per run.
     *
     * Carried as embedded resources rather than fetched, so a first import doesn't depend on the
     * network or on anything being put in place by hand. */
    private static void ExtractShippedDecoders()
    {
        if (ExtractedDecoders) return;
        ExtractedDecoders = true;

        var assembly = Assembly.GetExecutingAssembly();

        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith("dec.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var name = resource[(resource.LastIndexOf("Dependencies.", StringComparison.OrdinalIgnoreCase) + "Dependencies.".Length)..];
            var target = new FileInfo(Path.Combine(DecodersFolder.FullName, name));

            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;

                /* Length is enough to notice a build carrying a newer one */
                if (target.Exists && target.Length == stream.Length) continue;

                DecodersFolder.Create();

                using var file = new FileStream(target.FullName, FileMode.Create, FileAccess.Write);
                stream.CopyTo(file);

                Log.Information($"Wrote out audio decoder {target.Name}");
            }
            catch (Exception exception)
            {
                /* One already in place and locked is not worth failing an import over */
                Log.Warning($"Failed to write out {target.Name}: {exception.Message}");
            }
        }
    }

    /* Decoder for a format, null when there isn't one to be found. */
    private static (FileInfo File, bool UsesVgmStream)? FindDecoder(string extension)
    {
        lock (DecodersLock)
        {
            ExtractShippedDecoders();

            var dedicated = new FileInfo(Path.Combine(DecodersFolder.FullName, $"{extension}dec.exe"));
            if (dedicated.Exists) return (dedicated, false);

            /* RAD's formats read by their own decoder and by nothing else, so handing one to
             * vgmstream is a crash and a misleading failure rather than a fallback */
            if (RadFormats.Contains(extension))
            {
                Log.Warning($"Missing {extension}dec.exe, .{extension} can only be decoded by it");

                return null;
            }

            var vgmStream = new FileInfo(Path.Combine(DecodersFolder.FullName, VgmStream));

            return vgmStream.Exists ? (vgmStream, true) : null;
        }
    }

    /* Converts a cooked sound to wav next to where it came from. */
    public static bool TryConvertToWav(string sourcePath, out string wavPath)
    {
        wavPath = string.Empty;

        if (!File.Exists(sourcePath)) return false;

        var extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();

        var decoder = FindDecoder(extension);
        if (decoder is null)
        {
            Log.Warning($"No decoder for .{extension} in {DecodersFolder.FullName}, left as it was cooked");

            return false;
        }

        var target = Path.ChangeExtension(sourcePath, ".wav");

        try
        {
            /* vgmstream takes its output first and the input last, the dedicated decoders are -i/-o */
            var arguments = decoder.Value.UsesVgmStream
                ? $"-o \"{target}\" \"{sourcePath}\""
                : $"-i \"{sourcePath}\" -o \"{target}\"";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = decoder.Value.File.FullName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null) return false;

            /* Bounded because a decoder that never returns would hold the request open with it */
            if (!process.WaitForExit(30_000))
            {
                process.Kill(true);
                Log.Warning($"{decoder.Value.File.Name} timed out on {Path.GetFileName(sourcePath)}");

                return false;
            }

            if (process.ExitCode != 0 || !File.Exists(target))
            {
                /* A negative code is the process ending on a Windows status rather than answering,
                 * which usually means it never started: 0xC0000135 is a missing dll beside it */
                var reason = process.ExitCode < 0
                    ? $"didn't start (0x{process.ExitCode:X8}), check its dependencies are beside it"
                    : $"exited {process.ExitCode}";

                Log.Warning($"{decoder.Value.File.Name} {reason} on {Path.GetFileName(sourcePath)}");

                return false;
            }
        }
        catch (Exception exception)
        {
            Log.Warning($"Failed to decode {Path.GetFileName(sourcePath)}: {exception.Message}");

            return false;
        }

        wavPath = target;

        return true;
    }

    /* Mime type for a format, so a response says what it is carrying */
    public static string MimeTypeFor(string? audioFormat)
    {
        return ExtensionFor(audioFormat) switch
        {
            Wav => "audio/wav",
            "adpcm" => "audio/adpcm",
            "opus" => "audio/opus",
            "wem" => "application/vnd.wwise.wem",
            "rada" => "audio/vnd.rad.rada",
            "binka" => "audio/vnd.rad.binka",
            "at9" => "audio/vnd.sony.at9",
            _ => "audio/ogg"
        };
    }
}
