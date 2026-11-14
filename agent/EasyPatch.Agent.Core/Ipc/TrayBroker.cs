using System.Threading.Channels;
using EasyPatch.Agent.Core.Abstractions;
using EasyPatch.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EasyPatch.Agent.Core.Ipc;

/// <summary>
/// The service side of the tray connection: keeps one connection per signed-in session, pushes status, messages and
/// the restart prompt, and accepts exactly two kinds of input — "restart now" and "postpone" for the restart prompt
/// that is currently shown, with a postponement the prompt offered. Nothing else a tray sends has any effect.
/// </summary>
public sealed class TrayBroker : IUserInteraction
{
    private const int MaxPendingResponses = 16;

    private readonly Func<IReadOnlyCollection<int>> _signedInSessions;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Connection> _connections = [];
    private readonly List<RestartPromptResponse> _responses = [];
    private TrayStatus _status = new();
    private RestartPrompt? _prompt;
    private bool _suspended;

    /// <param name="signedInSessions">IDs of the sessions with a signed-in user.</param>
    public TrayBroker(Func<IReadOnlyCollection<int>> signedInSessions, ILogger<TrayBroker> logger)
    {
        _signedInSessions = signedInSessions ?? throw new ArgumentNullException(nameof(signedInSessions));
        _logger = logger;
    }

    /// <summary>True while an agent update keeps the trays closed.</summary>
    public bool TraysSuspended
    {
        get
        {
            lock (_lock)
            {
                return _suspended;
            }
        }
    }

    /// <summary>Sessions with a connected tray.</summary>
    public IReadOnlyCollection<int> ConnectedSessions
    {
        get
        {
            lock (_lock)
            {
                return _connections.Keys.ToList();
            }
        }
    }

    /// <summary>Serves one tray until it disconnects. <paramref name="sessionId"/> must come from the operating system
    /// (the pipe client's session), never from the tray itself. A newer connection from the same session replaces an older one.</summary>
    public async Task ServeAsync(Stream stream, int sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connection = new Connection(sessionId, cts);
        Connection? replaced;
        lock (_lock)
        {
            _connections.TryGetValue(sessionId, out replaced);
            _connections[sessionId] = connection;
            connection.Send(new TrayMessage { Type = TrayMessageType.Status, Status = _status });
            connection.Send(_suspended
                ? new TrayMessage { Type = TrayMessageType.Exit }
                : new TrayMessage { Type = TrayMessageType.Prompt, Prompt = _prompt });
        }

        replaced?.Close();
        _logger.LogDebug("Tray connected in session {SessionId}.", sessionId);
        try
        {
            var writer = WriteLoopAsync(stream, connection, cts.Token);
            var reader = ReadLoopAsync(stream, connection, cts.Token);
            await Task.WhenAny(writer, reader);
            await cts.CancelAsync();
            await Task.WhenAll(writer.ContinueWith(_ => { }, TaskScheduler.Default), reader.ContinueWith(_ => { }, TaskScheduler.Default));
        }
        finally
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(sessionId, out var current) && current == connection)
                {
                    _connections.Remove(sessionId);
                }
            }

            _logger.LogDebug("Tray in session {SessionId} disconnected.", sessionId);
        }
    }

    public UserSessionInfo GetSessions()
    {
        var signedIn = _signedInSessions();
        lock (_lock)
        {
            return new UserSessionInfo
            {
                SignedInUsers = signedIn.Count,
                TrayConnected = _connections.Keys.Count(signedIn.Contains),
            };
        }
    }

    public int Notify(UserNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_lock)
        {
            return _connections.Values.Count(c => c.Send(new TrayMessage { Type = TrayMessageType.Notice, Notice = notice }));
        }
    }

    public void SetRestartPrompt(RestartPrompt? prompt)
    {
        lock (_lock)
        {
            _prompt = prompt;
            if (prompt is null)
            {
                _responses.Clear();
            }

            Broadcast(new TrayMessage { Type = TrayMessageType.Prompt, Prompt = prompt });
        }
    }

    public IReadOnlyList<RestartPromptResponse> TakeResponses()
    {
        lock (_lock)
        {
            var list = _responses.ToList();
            _responses.Clear();
            return list;
        }
    }

    public void SetStatus(TrayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_lock)
        {
            if (status == _status)
            {
                return;
            }

            _status = status;
            Broadcast(new TrayMessage { Type = TrayMessageType.Status, Status = status });
        }
    }

    public void SuspendTrays(bool suspended)
    {
        lock (_lock)
        {
            _suspended = suspended;
            if (suspended)
            {
                Broadcast(new TrayMessage { Type = TrayMessageType.Exit });
            }
        }
    }

    /// <summary>Asks every tray to exit (service shutdown or uninstall).</summary>
    public void DisconnectAll()
    {
        lock (_lock)
        {
            Broadcast(new TrayMessage { Type = TrayMessageType.Exit });
        }
    }

    private void Broadcast(TrayMessage message)
    {
        foreach (var connection in _connections.Values)
        {
            connection.Send(message);
        }
    }

    private void Handle(Connection connection, TrayMessage message)
    {
        lock (_lock)
        {
            var prompt = _prompt;
            if (prompt is null || message.JobId != prompt.JobId || _responses.Count >= MaxPendingResponses)
            {
                return; // nothing to answer, or an answer to a prompt that is gone
            }

            switch (message.Type)
            {
                case TrayMessageType.RestartNow:
                    _responses.Add(new RestartPromptResponse(prompt.JobId, RestartNow: true, PostponeMinutes: null, connection.SessionId));
                    _logger.LogInformation("A user in session {SessionId} chose to restart now.", connection.SessionId);
                    break;
                case TrayMessageType.Postpone when prompt.Kind == RestartPromptKind.Prompt && message.Minutes is { } minutes &&
                                                   prompt.PostponeOptions.Contains(minutes):
                    _responses.Add(new RestartPromptResponse(prompt.JobId, RestartNow: false, minutes, connection.SessionId));
                    break;
                default:
                    _logger.LogDebug("Ignored {Type} message from the tray in session {SessionId}.", message.Type, connection.SessionId);
                    break;
            }
        }
    }

    private static async Task WriteLoopAsync(Stream stream, Connection connection, CancellationToken cancellationToken)
    {
        await foreach (var message in connection.Outgoing.Reader.ReadAllAsync(cancellationToken))
        {
            await TrayFrames.WriteAsync(stream, message, cancellationToken);
        }
    }

    private async Task ReadLoopAsync(Stream stream, Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (await TrayFrames.ReadAsync(stream, cancellationToken) is { } message)
            {
                Handle(connection, message);
            }
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning("Disconnected the tray in session {SessionId}: {Message}", connection.SessionId, ex.Message);
        }
    }

    private sealed class Connection(int sessionId, CancellationTokenSource cts)
    {
        public int SessionId { get; } = sessionId;

        public Channel<TrayMessage> Outgoing { get; } = Channel.CreateBounded<TrayMessage>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public bool Send(TrayMessage message) => Outgoing.Writer.TryWrite(message);

        public void Close()
        {
            Outgoing.Writer.TryComplete();
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
