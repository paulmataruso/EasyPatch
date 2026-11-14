using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using EasyPatch.Agent.Core.Abstractions;
using EasyPatch.Contracts.Json;

namespace EasyPatch.Agent.Core.Ipc;

public enum TrayMessageType
{
    // service -> tray
    Status,
    Notice,
    Prompt,

    /// <summary>The tray should exit (the service is stopping or being uninstalled).</summary>
    Exit,

    // tray -> service
    RestartNow,
    Postpone,
}

/// <summary>One message on the tray pipe. Only the fields relevant to <see cref="Type"/> are set.</summary>
public sealed record TrayMessage
{
    public required TrayMessageType Type { get; init; }
    public TrayStatus? Status { get; init; }
    public UserNotice? Notice { get; init; }

    /// <summary>For <see cref="TrayMessageType.Prompt"/>; null removes the restart prompt.</summary>
    public RestartPrompt? Prompt { get; init; }

    public Guid? JobId { get; init; }
    public int? Minutes { get; init; }
}

/// <summary>Length-prefixed (32-bit little-endian) UTF-8 JSON frames, at most <see cref="MaxFrameBytes"/>.</summary>
public static class TrayFrames
{
    /// <summary>\\.\pipe\EasyPatchAgent</summary>
    public const string PipeName = "EasyPatchAgent";

    public const int MaxFrameBytes = 16 * 1024;

    /// <summary>The rights the tray requests when it opens the pipe.</summary>
    /// <remarks>Not GENERIC_READ | GENERIC_WRITE (what a <see cref="System.IO.Pipes.PipeDirection"/> constructor asks
    /// for): GENERIC_WRITE maps to FILE_APPEND_DATA, which on a pipe is the right to create pipe instances, and
    /// signed-in users are deliberately not given that right, so Windows would refuse the connection.</remarks>
    public const PipeAccessRights ClientAccess =
        PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize;

    /// <summary>The rights interactive users have on the pipe: everything <see cref="ClientAccess"/> requests, but not
    /// <see cref="PipeAccessRights.CreateNewInstance"/>, so no user process can serve the pipe.</summary>
    public const PipeAccessRights InteractiveUserAccess = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    public static async Task WriteAsync(Stream stream, TrayMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options);
        if (json.Length > MaxFrameBytes)
        {
            throw new InvalidDataException("Tray message too large.");
        }

        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads the next message; null at end of stream. Throws <see cref="InvalidDataException"/> on a bad frame.</summary>
    public static async Task<TrayMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var read = 0;
        while (read < header.Length)
        {
            var n = await stream.ReadAsync(header.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                return read == 0 ? null : throw new InvalidDataException("Truncated tray frame.");
            }

            read += n;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException("Invalid tray frame length.");
        }

        var body = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(body, cancellationToken);
            return JsonSerializer.Deserialize<TrayMessage>(body, ContractJson.Options) ?? throw new InvalidDataException("Empty tray message.");
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Truncated tray frame.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed tray message.", ex);
        }
    }
}
