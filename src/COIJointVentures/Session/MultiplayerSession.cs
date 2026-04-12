using System;
using System.Collections.Generic;
using BepInEx.Logging;
using COIJointVentures.Chat;
using COIJointVentures.Commands;
using COIJointVentures.Integration;
using COIJointVentures.Networking;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession : IDisposable
{
    // ── Infrastructure ────────────────────────────────────────────────────────
    private readonly ManualLogSource _log;
    private readonly ICommandCodec _codec;
    private readonly INetworkTransport _transport;
    private readonly ChatCommandHandler _chatCommandHandler;
    private readonly LatencyTracker _latencyTracker;

    // ── Session identity ──────────────────────────────────────────────────────
    public MultiplayerMode Mode { get; private set; }
    public ConnectionState State { get; private set; }
    public string LocalPeerId { get; private set; } = string.Empty;
    public string HostPeerId { get; private set; } = string.Empty;
    public string StatusMessage { get; private set; } = string.Empty;
    public string LocalPlayerName { get; private set; } = string.Empty;
    public DateTime? ActiveSince { get; private set; }  // when we went active, used for the startup grace period
    public bool HostDisconnected { get; private set; }  // set when the host drops us — the plugin checks this and boots us to menu

    // ── Player registry (see MultiplayerSession.Players.cs) ──────────────────
    private readonly HashSet<string> _activePeers = new();
    private readonly HashSet<string> _pendingPeers = new();
    private readonly Dictionary<string, string> _peerNames = new();
    private readonly Dictionary<string, int> _peerColors = new();
    private readonly List<PlayerInfo> _playerList = new();
    private int _nextColorIndex;
    private int _localColorIndex; // assigned by host, used for local waypoint display

    // ── Join coordination (see MultiplayerSession.Join.cs) ───────────────────
    private ServerConfig? _serverConfig;
    private JoinCoordinator? _joinCoordinator;
    private SaveManagerBridge? _saveBridge;
    public JoinCoordinator? JoinCoordinator => _joinCoordinator;
    public bool IsJoinSyncActive { get; private set; }
    public string JoinSyncPlayerName { get; private set; } = string.Empty;

    // ── Save transfer (see MultiplayerSession.Save.cs) ───────────────────────
    private SaveFileManager? _saveManager;
    private string? _currentSavePath;
    private byte[]? _receivedSaveData;
    private string? _receivedSavePath;
    private byte[]? _saveChunkBuffer;       // client-side chunked save reassembly
    private int _saveChunksReceived;
    private int _saveChunksExpected;
    private readonly Queue<(string peerId, byte[] payload)> _pendingSends = new();  // burst sender queue
    public byte[]? ReceivedSaveData => _receivedSaveData;
    public string? ReceivedSavePath => _receivedSavePath;

    // ── Command pipeline (see MultiplayerSession.Commands.cs) ────────────────
    private readonly Queue<PendingCommand> _pendingCommands = new();
    private readonly HashSet<Guid> _outboundCommandIds = new();
    private readonly HashSet<Guid> _seenCommandIds = new();
    private readonly Dictionary<Guid, PendingAck> _pendingAcks = new();
    private readonly Dictionary<Guid, CommandReassemblyBuffer> _commandChunkBuffers = new();
    private readonly HashSet<string> _observedNativeCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Guid CommandId, byte[] EncodedPayload)> _frameBuffer = new();  // per-tick batch accumulator (client only)
    private long _nextSequence;
    private bool _simulateDropNextPacket;
    private const double AckTimeoutSeconds = 3.0;
    private const int MaxCommandSize = 4 * 1024 * 1024; // 4 MB hard cap

    // ── Resync / desync (see MultiplayerSession.Resync.cs) ───────────────────
    public DesyncState DesyncState { get; private set; }
    private long _lastProcessedHostSequence = -1;
    private int _checksumTickCounter;
    private bool _minorResyncInFlight;
    private DateTime _minorResyncRequestTime;
    private readonly Runtime.CommandHistory _commandHistory = new();
    private readonly Dictionary<long, int> _pendingHostChecksums = new();

    /// <summary>
    /// Optional probe wired by <see cref="Plugin"/> to contribute a game-model
    /// value to the state checksum.  Set to null until the sim world is available.
    /// </summary>
    public static Func<int>? GameStateProbe { get; set; }

    // ─────────────────────────────────────────────────────────────────────────

    public MultiplayerSession(ManualLogSource log, ICommandCodec codec, INetworkTransport transport)
    {
        _log = log;
        _codec = codec;
        _transport = transport;
        _chatCommandHandler = new ChatCommandHandler(this, _log);
        _latencyTracker = new LatencyTracker(this, _transport, _log);
        _transport.MessageReceived += OnTransportMessageReceived;
        _transport.ClientDisconnected += OnClientDisconnected;
    }

    public void StartAsHost(string peerId, ServerConfig config, SaveFileManager saveManager, string? currentSavePath)
    {
        _pendingCommands.Clear();
        _pendingHostChecksums.Clear();
        DesyncState = DesyncState.None;
        _commandHistory.Clear();
        _commandChunkBuffers.Clear();
        _serverConfig = config;
        _saveManager = saveManager;
        _currentSavePath = currentSavePath;

        _saveBridge = new SaveManagerBridge(_log);
        _joinCoordinator = new JoinCoordinator(_log, _saveBridge, saveManager);
        _joinCoordinator.SendSaveToClients = SendSaveToClients;
        _joinCoordinator.BroadcastJoinSyncBegin = () =>
        {
            _transport.Broadcast(ProtocolCodec.WrapJoinSyncBegin(_joinCoordinator.JoiningPlayerNames));
        };
        _joinCoordinator.BroadcastJoinSyncEnd = () =>
        {
            _transport.Broadcast(ProtocolCodec.WrapJoinSyncEnd());
        };
        _joinCoordinator.Unpause = () =>
        {
            var scheduler = PluginRuntime.Scheduler;
            if (scheduler != null)
            {
                scheduler.ScheduleInputCmd(new Mafi.Core.Simulation.SetSimPauseStateCmd(isPaused: false));
            }
        };
        _joinCoordinator.HasPendingSends = () => HasPendingSends;

        LocalPeerId = peerId;
        HostPeerId = peerId;
        LocalPlayerName = ResolvePeerName(peerId);
        _transport.StartHost(LocalPeerId);
        Mode = MultiplayerMode.Host;
        State = ConnectionState.Hosting;
        StatusMessage = $"Hosting '{config.ServerName}'" +
                        (config.HasPassword ? " (password protected)" : "");
        _log.LogInfo(StatusMessage);
        BroadcastPlayerList();
    }

    public void StartAsClient(string localPeerId, string hostPeerId, string playerName, string password)
    {
        _pendingCommands.Clear();
        _pendingHostChecksums.Clear();
        DesyncState = DesyncState.None;
        _lastProcessedHostSequence = -1;
        _minorResyncInFlight = false;
        _minorResyncRequestTime = default;
        _pendingAcks.Clear();
        LocalPeerId = localPeerId;
        HostPeerId = hostPeerId;
        LocalPlayerName = playerName;
        State = ConnectionState.Connecting;
        StatusMessage = $"Connecting to {hostPeerId}...";

        try
        {
            _transport.StartClient(LocalPeerId, HostPeerId);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Idle;
            Mode = MultiplayerMode.None;
            StatusMessage = $"Connection failed: {ex.Message}";
            _log.LogWarning(StatusMessage);
            throw;
        }

        // connected, fire off join request
        State = ConnectionState.WaitingForAccept;
        StatusMessage = "Connected, sending join request...";

        var joinRequest = new JoinRequest
        {
            PlayerName = playerName,
            Password = password,
            ModVersion = Plugin.PluginVersion
        };

        _transport.SendToHost(ProtocolCodec.WrapJoinRequest(joinRequest));
        _log.LogInfo($"Sent join request as '{playerName}'.");
    }

    public void Disconnect()
    {
        _transport.Dispose();
        Mode = MultiplayerMode.None;
        State = ConnectionState.Idle;
        StatusMessage = "Disconnected.";
        HostDisconnected = false;
        IsJoinSyncActive = false;
        JoinSyncPlayerName = string.Empty;
        _pendingHostChecksums.Clear();
        DesyncState = DesyncState.None;
        _lastProcessedHostSequence = -1;
        _minorResyncInFlight = false;
        _minorResyncRequestTime = default;
        _commandHistory.Clear();
        _pendingCommands.Clear();
        _pendingAcks.Clear();
        _commandChunkBuffers.Clear();
        lock (_activePeers) { _activePeers.Clear(); }
        lock (_pendingPeers) { _pendingPeers.Clear(); }
        _receivedSaveData = null;
        _receivedSavePath = null;
        _log.LogInfo("Session disconnected.");
    }

    public void Dispose()
    {
        _transport.MessageReceived -= OnTransportMessageReceived;
        _transport.ClientDisconnected -= OnClientDisconnected;
    }

    public void TickJoinCoordinator()
    {
        _joinCoordinator?.Tick();
    }

    public void TickLatencyTracker()
    {
        if (_latencyTracker.Tick())
            BroadcastPlayerList();
    }

    // ── Message dispatch ──────────────────────────────────────────────────────

    private void OnTransportMessageReceived(TransportMessage message)
    {
        if (message.Payload.Length == 0)
        {
            return;
        }

        var msgType = ProtocolCodec.ReadType(message.Payload);
        var payload = ProtocolCodec.ReadPayload(message.Payload);

        switch (msgType)
        {
            case ProtocolMessageType.JoinRequest:
                HandleJoinRequest(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.JoinAccepted:
                HandleJoinAccepted(payload);
                break;
            case ProtocolMessageType.JoinRejected:
                HandleJoinRejected(payload);
                break;
            case ProtocolMessageType.SaveData:
                HandleSaveData(payload);
                break;
            case ProtocolMessageType.SaveChunk:
                HandleSaveChunk(payload);
                break;
            case ProtocolMessageType.SaveComplete:
                HandleSaveComplete();
                break;
            case ProtocolMessageType.ClientReady:
                HandleClientReady(message.SenderPeerId);
                break;
            case ProtocolMessageType.JoinSyncBegin:
                IsJoinSyncActive = true;
                JoinSyncPlayerName = ProtocolCodec.DecodeJoinSyncBegin(payload);
                _log.LogInfo($"[JOIN-SYNC] Join sync active: '{JoinSyncPlayerName}' is joining. Pausing sim.");
                // pause while they're joining
                var pauseScheduler = Runtime.PluginRuntime.Scheduler;
                if (pauseScheduler != null)
                {
                    pauseScheduler.ScheduleInputCmd(new Mafi.Core.Simulation.SetSimPauseStateCmd(isPaused: true));
                }
                break;
            case ProtocolMessageType.JoinSyncEnd:
                IsJoinSyncActive = false;
                JoinSyncPlayerName = string.Empty;
                _log.LogInfo("[JOIN-SYNC] Join sync ended. Unpausing sim.");
                // unpause, they're in
                var unpauseScheduler = Runtime.PluginRuntime.Scheduler;
                if (unpauseScheduler != null)
                {
                    unpauseScheduler.ScheduleInputCmd(new Mafi.Core.Simulation.SetSimPauseStateCmd(isPaused: false));
                }
                break;
            case ProtocolMessageType.ChatMessage:
                HandleChatMessage(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.StateChecksum:
                HandleStateChecksum(payload);
                break;
            case ProtocolMessageType.GameCommand:
                HandleGameCommand(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.BatchedCommands:
                HandleBatchedCommands(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.PlayerList:
                HandlePlayerList(payload);
                break;
            case ProtocolMessageType.Waypoint:
                HandleWaypoint(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.MinorResyncRequest:
                HandleMinorResyncRequest(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.MinorResyncResponse:
                HandleMinorResyncResponse(payload);
                break;
            case ProtocolMessageType.MajorResyncRequest:
                HandleMajorResyncRequest(message.SenderPeerId);
                break;
            case ProtocolMessageType.CommandChunkStart:
                HandleCommandChunkStart(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.CommandChunk:
                HandleCommandChunk(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.PingRequest:
                _latencyTracker.HandlePingRequest(ProtocolCodec.DecodePing(payload));
                break;
            case ProtocolMessageType.PingResponse:
                if (_latencyTracker.HandlePingResponse(ProtocolCodec.DecodePing(payload)))
                    BroadcastPlayerList();
                break;
            default:
                _log.LogWarning($"Unknown protocol message type: {(byte)msgType} from '{message.SenderPeerId}'.");
                break;
        }
    }

    // ── Disconnection ─────────────────────────────────────────────────────────

    // fired when someone drops — could be a client (if we're host) or the host (if we're client)
    private void OnClientDisconnected(string peerId)
    {
        bool wasActive;
        lock (_activePeers) { wasActive = _activePeers.Remove(peerId); }
        lock (_pendingPeers) { _pendingPeers.Remove(peerId); }

        if (wasActive && Mode == MultiplayerMode.Host)
        {
            _log.LogInfo($"Active client '{peerId}' disconnected.");
            StatusMessage = $"Hosting — {_activePeers.Count} player(s) connected.";
            PluginRuntime.Chat.AddSystem($"{ResolvePeerName(peerId)} left the game.");
        }

        // if we're a client and the host dropped, we're fucked — flag it
        if (Mode == MultiplayerMode.Client && string.Equals(peerId, HostPeerId, StringComparison.Ordinal))
        {
            _log.LogInfo("Host disconnected. Session is dead.");
            StatusMessage = "Host disconnected.";
            HostDisconnected = true;
        }

        _joinCoordinator?.OnClientDisconnected(peerId);

        if (Mode == MultiplayerMode.Host)
            BroadcastPlayerList();
    }
}
