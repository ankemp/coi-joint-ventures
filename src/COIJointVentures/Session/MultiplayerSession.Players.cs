using System.Collections.Generic;
using COIJointVentures.Networking;
using COIJointVentures.Networking.Protocol;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession
{
    public IReadOnlyCollection<string> ActivePeers
    {
        get
        {
            lock (_activePeers)
            {
                return new List<string>(_activePeers);
            }
        }
    }

    public IReadOnlyCollection<string> PendingPeers
    {
        get
        {
            lock (_pendingPeers)
            {
                return new List<string>(_pendingPeers);
            }
        }
    }

    /// <summary>
    /// Full player list with display names and color indices.
    /// On the host this is built from local state. On clients it's
    /// received from the host via PlayerList messages.
    /// </summary>
    public List<PlayerInfo> PlayerList
    {
        get
        {
            lock (_playerList)
            {
                return new List<PlayerInfo>(_playerList);
            }
        }
    }

    private int GetOrAssignColor(string peerId)
    {
        lock (_peerColors)
        {
            if (!_peerColors.TryGetValue(peerId, out var idx))
            {
                idx = _nextColorIndex++;
                _peerColors[peerId] = idx;
            }
            return idx;
        }
    }

    private string ResolvePeerName(string peerId)
    {
        lock (_peerNames)
        {
            if (_peerNames.TryGetValue(peerId, out var name))
            {
                return name;
            }
        }

        try
        {
            if (ulong.TryParse(peerId, out var steamIdValue))
            {
                var friend = new Steamworks.Friend(new Steamworks.SteamId { Value = steamIdValue });
                if (!string.IsNullOrEmpty(friend.Name))
                {
                    return friend.Name;
                }
            }
        }
        catch
        {
        }

        return peerId;
    }

    /// <summary>
    /// Host builds the player list from local state and pushes it to all clients.
    /// Also updates our own _playerList so the host UI stays current.
    /// </summary>
    public void BroadcastPlayerList()
    {
        if (Mode != MultiplayerMode.Host) return;

        var list = new List<PlayerInfo>();

        // host is always first
        list.Add(new PlayerInfo
        {
            Name = LocalPlayerName,
            ColorIndex = GetOrAssignColor(LocalPeerId),
            IsPending = false
        });

        lock (_activePeers)
        {
            foreach (var peer in _activePeers)
            {
                list.Add(new PlayerInfo
                {
                    Name = ResolvePeerName(peer),
                    ColorIndex = GetOrAssignColor(peer),
                    IsPending = false
                });
            }
        }

        lock (_pendingPeers)
        {
            foreach (var peer in _pendingPeers)
            {
                list.Add(new PlayerInfo
                {
                    Name = ResolvePeerName(peer),
                    ColorIndex = GetOrAssignColor(peer),
                    IsPending = true
                });
            }
        }

        lock (_playerList)
        {
            _playerList.Clear();
            _playerList.AddRange(list);
        }

        _transport.Broadcast(ProtocolCodec.WrapPlayerList(list));

        // update lobby metadata so the server browser shows the right count
        if (_transport is SteamTransport steam)
            steam.SetLobbyData("players", list.Count.ToString());
    }

    private void HandlePlayerList(byte[] payload)
    {
        var list = ProtocolCodec.DecodePlayerList(payload);
        lock (_playerList)
        {
            _playerList.Clear();
            _playerList.AddRange(list);
        }
    }
}
