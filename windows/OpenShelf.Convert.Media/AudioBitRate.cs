namespace OpenShelf.Convert;

/// <summary>The facts about a source's sound that decide how many bits a lossy copy needs. Port of SourceAudio.</summary>
/// <param name="Channels">Number of channels.</param>
/// <param name="BitRate">Bits per second, or null when the container does not say.</param>
/// <param name="Codec">The lossy codec it is in, when <see cref="AudioBitRate"/> knows it.</param>
public sealed record SourceAudio(int Channels, int? BitRate = null, AudioCodec? Codec = null);

/// <summary>Lossy audio codecs the bitrate policy knows.</summary>
public enum AudioCodec { Aac, Opus, Vorbis, Mp3, Wma }

/// <summary>One bitrate policy for every lossy audio output: what the codec needs to sound good, but never more than the
/// source had to give. Port of AudioBitRate.swift.</summary>
public static class AudioBitRate
{
    /// <summary>Bitrates encoders and players commonly use, in kb/s.</summary>
    public static readonly IReadOnlyList<int> Steps = [24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    /// <summary>The codec for ffprobe's codec_name, if it is one the policy knows.</summary>
    public static AudioCodec? CodecFromFfprobe(string name) => name switch
    {
        "aac" => AudioCodec.Aac,
        "opus" => AudioCodec.Opus,
        "vorbis" => AudioCodec.Vorbis,
        "mp3" => AudioCodec.Mp3,
        "wmav1" or "wmav2" => AudioCodec.Wma,
        _ => null,
    };

    /// <summary>Bits needed for what AAC, Opus or Vorbis do with one.</summary>
    public static double BitsPerAacBit(this AudioCodec codec) => codec is AudioCodec.Mp3 or AudioCodec.Wma ? 1.25 : 1;

    /// <summary>Stereo floor and ceiling in kb/s: below the floor it sounds poor, above the ceiling no one can hear a difference.</summary>
    public static (int Floor, int Ceiling) KbpsRange(this AudioCodec codec) => codec switch
    {
        AudioCodec.Opus => (32, 128),
        AudioCodec.Aac => (64, 192),
        AudioCodec.Vorbis => (64, 160),
        AudioCodec.Mp3 => (64, 192),
        _ => (64, 160),
    };

    /// <summary>Bits per second for a lossy copy of <paramref name="source"/> in <paramref name="codec"/>.
    /// A clean source gets the codec's ceiling, halved for mono; a lossy one gets what matches its own rate.</summary>
    public static int Target(AudioCodec codec, SourceAudio? source, int? limit = null)
    {
        var (floor, ceiling) = codec.KbpsRange();
        if (limit is { } cap) ceiling = Math.Min(ceiling, cap / 1000);
        if (source?.Channels == 1)
        {
            ceiling /= 2;
            floor = floor * 3 / 4;
        }
        ceiling = Math.Max(ceiling, floor);

        var kbps = ceiling;
        if (source is { BitRate: > 0 } rated)
        {
            var sourceFactor = rated.Codec?.BitsPerAacBit() ?? 1;
            var needed = rated.BitRate!.Value / 1000.0 / sourceFactor * codec.BitsPerAacBit();
            kbps = Math.Min(ceiling, Math.Max(floor, (int)Math.Round(needed, MidpointRounding.AwayFromZero)));
        }
        // Round down to a common rate, but never below the codec's floor.
        var step = Steps.LastOrDefault(s => s <= kbps);
        if (step == 0) step = floor;
        return Math.Max(step, floor) * 1000;
    }

    /// <summary>The ffmpeg argument for <see cref="Target"/>, e.g. "96k".</summary>
    public static string Argument(AudioCodec codec, SourceAudio? source) => $"{Target(codec, source) / 1000}k";

    /// <summary>The ffmpeg -b:a value: the fixed rate from the settings (lowered for mono where the encoder insists), or the policy's.</summary>
    public static string Argument(AudioOptions options, AudioCodec codec, SourceAudio? source)
    {
        if (options.BitrateKbps is not { } kbps) return Argument(codec, source);
        return $"{FixedKbps(kbps, codec, source?.Channels ?? 2)}k";
    }

    /// <summary>A fixed rate lowered to what the codec accepts for <paramref name="channels"/>.</summary>
    public static int FixedKbps(int kbps, AudioCodec codec, int channels)
    {
        if (channels != 1) return kbps;
        return MonoLimit(codec) is { } limit ? Math.Min(kbps, limit) : kbps;
    }

    /// <summary>The most a codec takes for one channel, in kb/s, when below what is offered.</summary>
    public static int? MonoLimit(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac or AudioCodec.Opus => 256,
        AudioCodec.Vorbis => 192,
        _ => null,
    };
}
