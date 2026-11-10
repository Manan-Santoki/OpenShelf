using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenShelf.Convert;

namespace OpenShelf.Automation;

/// <summary>What arrived on the single-instance pipe.</summary>
public abstract record PipeRequest;

/// <summary>The legacy message (one byte, 1) or a bare connection: show Settings.</summary>
public sealed record ShowSettingsRequest : PipeRequest;

public sealed record CommandRequest(AutomationCommand Command) : PipeRequest;

/// <summary>
/// The wire format on the single-instance pipe "OpenShelf.Activate".
/// <code>
/// legacy:   01                                  show Settings (the only thing old copies send)
/// command:  02 | length (int32 LE) | UTF-8 JSON {"v":1,"command":"convert","format":"jpg","paths":["C:\\a.png"]}
/// </code>
/// An old server sees a connection and shows Settings, so a new client talking to an old app still does something sensible.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "OpenShelf.Activate";
    public const byte ShowSettingsByte = 1;
    public const byte CommandByte = 2;
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    public static string ToJson(AutomationCommand command)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("command", command.Kind.ToString().ToLowerInvariant());
            if (command.Format is { } f) w.WriteString("format", f.Id());
            w.WriteStartArray("paths");
            foreach (var p in command.Paths) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Null for anything malformed or unknown.</summary>
    public static AutomationCommand? FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) return null;
            if (!Enum.TryParse<CommandKind>(c.GetString(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) return null;
            if (!root.TryGetProperty("paths", out var ps) || ps.ValueKind != JsonValueKind.Array) return null;
            var paths = new List<string>();
            foreach (var p in ps.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString())) return null;
                paths.Add(p.GetString()!);
            }
            if (paths.Count == 0) return null;
            OutputFormat? format = null;
            if (kind == CommandKind.Convert)
            {
                if (!root.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String) return null;
                format = CommandLine.ParseFormat(f.GetString() ?? "");
                if (format is null) return null;
            }
            return new AutomationCommand(kind, paths, format);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The bytes a client writes for a command.</summary>
    public static byte[] Frame(AutomationCommand command)
    {
        var json = Encoding.UTF8.GetBytes(ToJson(command));
        var frame = new byte[5 + json.Length];
        frame[0] = CommandByte;
        BitConverter.TryWriteBytes(frame.AsSpan(1, 4), json.Length); // little-endian on every Windows target
        json.CopyTo(frame, 5);
        return frame;
    }

    /// <summary>Reads one request. A first byte of 1, or a client that connects and closes, means show Settings.
    /// Returns null for a malformed message (ignore it).</summary>
    public static async Task<PipeRequest?> ReadAsync(Stream stream, CancellationToken token)
    {
        var first = new byte[1];
        if (await stream.ReadAsync(first, token) == 0) return new ShowSettingsRequest();
        if (first[0] == ShowSettingsByte) return new ShowSettingsRequest();
        if (first[0] != CommandByte) return null;

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxJsonBytes) return null;
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return FromJson(Encoding.UTF8.GetString(body)) is { } command ? new CommandRequest(command) : null;
    }

    /// <summary>Server loop helper: waits for one client on <paramref name="pipeName"/> and reads its request.
    /// A client that stalls mid-message is dropped after <paramref name="readTimeout"/>. Returns null for a bad message.</summary>
    public static async Task<PipeRequest?> AcceptAsync(string pipeName, TimeSpan readTimeout, CancellationToken token)
    {
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(readTimeout);
        try { return await ReadAsync(server, timeout.Token); }
        catch (Exception e) when (e is OperationCanceledException or EndOfStreamException or IOException)
        {
            if (token.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>Sends a command (or, when null, the legacy "show Settings" byte) to the running copy. False if none answered.</summary>
    public static async Task<bool> SendAsync(AutomationCommand? command, int connectTimeoutMs = 800, string pipeName = PipeName)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            await client.ConnectAsync(connectTimeoutMs);
            if (command is null) client.WriteByte(ShowSettingsByte);
            else await client.WriteAsync(Frame(command));
            await client.FlushAsync();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
