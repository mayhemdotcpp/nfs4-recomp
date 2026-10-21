using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Netplay;

// Every car has one owner who simulates it and sends its state to the others, who copy it onto
// their copy of that car: each player owns their own car, the host also owns every AI and traffic
// car. Cars are identified by their index in the race's car list. The host is player 0 and relays
// every guest's packets to the other guests, so guests only ever talk to the host.
//
// Between races the host keeps a lobby: guests send Join every half second (also the heartbeat),
// flagged ready while they wait on their car select, and the host answers with their player number.
// Guests it hasn't heard from in a while are dropped and the rest renumbered. While a race runs
// the lobby is closed: nobody new joins and only its members get answers about starting races.
//
// packets (little endian):
//   Join     [1, ready]
//   Welcome  [2, player, setup]          setup: the host's last race, a new guest counts from there
//   State    [3, race, owner, car, u32 seq, u32 input, u16 count, count * u32 word]
//   Ready    [4, race, player]
//   SetupReq [5, setup]
//   Setup    [6, setup, players, u16 total, u16 offset, u16 length, length * byte]
//   Car      [7, setup, player, count * byte]   a guest's pick for the race being loaded
//   Starting [8, setup]                         answers a SetupReq for a race the host started loading
//   Busy     [9]                                answers a Join while a race runs
//   Pause    [10, race, pick, seq]              the host quit (1) or restarted (2) the race, everybody does
public sealed class Session
{
    private const byte Join = 1, Welcome = 2, State = 3, Ready = 4, SetupReq = 5, Setup = 6, Car = 7, Starting = 8, Busy = 9, PauseMenu = 10;
    public const byte PickQuit = 1, PickRestart = 2;
    public const int MaxPlayers = 8;
    public const int MaxCars = 9;
    private const int SetupChunk = 1024;
    private const long BusyMemoryMs = 2000;
    private const long GuestTimeoutMs = 5000;

    public readonly bool IsHost;
    public int LocalPlayer { get; private set; }
    public int Players { get; private set; }

    private sealed class Guest
    {
        public IPEndPoint EndPoint = null!;
        public long LastSeen;
        public bool Ready;
    }

    private readonly UdpClient _udp;
    private readonly IPEndPoint _hostEndPoint;
    private readonly List<Guest> _guests = [];
    private readonly object _gate = new();

    private bool _inRace;
    private bool _lobbyReady;

    private volatile bool _closed;

    private byte _pausePick;
    private byte _pickSeq;
    private long _busyAt = long.MinValue / 2;

    private byte _race;
    private int _ready;
    private uint _seq;

    private byte _setupId;
    private byte _startingId;
    private bool _hostStarted;
    private byte[] _setup;
    private byte[] _setupParts;
    private int _setupHave;

    private readonly uint[] _inputs = new uint[MaxPlayers];
    private readonly uint[] _inputSeq = new uint[MaxPlayers];
    private readonly uint[] _stateSeq = new uint[MaxCars];
    private readonly bool[] _stateFresh = new bool[MaxCars];
    private readonly uint[][] _states = new uint[MaxCars][];
    private readonly byte[][] _cars = new byte[MaxPlayers][];

    public Session(bool host, string address)
    {
        IsHost = host;
        Players = 1;

        var (ip, port) = ParseAddress(address);
        if (host)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            LocalPlayer = 0;
        }
        else
        {
            _udp = new UdpClient(0);
            _hostEndPoint = new IPEndPoint(ip, port);
            LocalPlayer = -1;
        }

        // windows reports ICMP port unreachable as a receive error, which would kill the socket
        try { _udp.Client.IOControl(-1744830452, [0], null); } catch { }

        new Thread(ReceiveLoop) { IsBackground = true, Name = "netplay-recv" }.Start();
        if (!host) new Thread(JoinLoop) { IsBackground = true, Name = "netplay-join" }.Start();

        Console.WriteLine(host ? $"[netplay] hosting on port {port}" : $"[netplay] joining {ip}:{port}");
    }

    public void Close()
    {
        _closed = true;
        _udp.Close();
    }

    public void SendPausePick(byte pick)
    {
        byte race, seq;
        lock (_gate)
        {
            race = _race;
            seq = ++_pickSeq;
        }

        for (var i = 0; i < 3; i++) Broadcast([PauseMenu, race, pick, seq], null);
    }

    public byte TakePausePick()
    {
        lock (_gate)
        {
            var pick = _pausePick;
            _pausePick = 0;
            return pick;
        }
    }

    public bool AllReady
    {
        get
        {
            lock (_gate) return _ready == (1 << Players) - 1;
        }
    }

    public void NewRace()
    {
        lock (_gate)
        {
            _race = _setupId;
            _ready = LocalPlayer >= 0 ? 1 << LocalPlayer : 0;
            Array.Clear(_inputs);
            Array.Clear(_inputSeq);
            Array.Clear(_stateSeq);
            Array.Clear(_stateFresh);
        }
    }

    public void SendReady()
    {
        if (LocalPlayer < 0) return;
        byte race;
        lock (_gate) race = _race;
        Broadcast([Ready, race, (byte)LocalPlayer], null);
    }

    public void SendState(int car, uint input, uint[] words)
    {
        if (LocalPlayer < 0) return;
        var packet = new byte[14 + words.Length * 4];
        packet[0] = State;
        lock (_gate) packet[1] = _race;
        packet[2] = (byte)LocalPlayer;
        packet[3] = (byte)car;
        BitConverter.TryWriteBytes(packet.AsSpan(4), ++_seq);
        BitConverter.TryWriteBytes(packet.AsSpan(8), input);
        BitConverter.TryWriteBytes(packet.AsSpan(12), (ushort)words.Length);
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(packet.AsSpan(14 + i * 4), words[i]);
        Broadcast(packet, null);
    }

    public uint LatestInput(int player)
    {
        lock (_gate) return _inputs[player];
    }

    public bool TakeState(int car, uint[] words)
    {
        lock (_gate)
        {
            if (!_stateFresh[car] || _states[car] == null || _states[car].Length != words.Length) return false;
            _stateFresh[car] = false;
            Array.Copy(_states[car], words, words.Length);
            return true;
        }
    }

    public (int Joined, int Ready) Lobby()
    {
        lock (_gate)
        {
            if (!_inRace)
            {
                var now = Environment.TickCount64;
                var dropped = _guests.RemoveAll(g => now - g.LastSeen > GuestTimeoutMs);
                if (dropped > 0) Console.WriteLine($"[netplay] {dropped} player(s) left, {_guests.Count} still in the lobby");
            }

            var ready = 0;
            foreach (var g in _guests)
                if (g.Ready)
                    ready++;
            return (_guests.Count, ready);
        }
    }

    public void CloseLobby()
    {
        lock (_gate) _inRace = true;
    }

    public void EndRace()
    {
        lock (_gate)
        {
            _inRace = false;
            _lobbyReady = false;
            foreach (var g in _guests) g.Ready = false;
        }
    }

    public bool HostBusy
    {
        get
        {
            lock (_gate) return LocalPlayer < 0 && Environment.TickCount64 - _busyAt < BusyMemoryMs;
        }
    }

    public void SetLobbyReady(bool ready)
    {
        lock (_gate) _lobbyReady = ready;
    }

    public void LockPlayers()
    {
        lock (_gate)
        {
            _inRace = true;
            Players = _guests.Count + 1;
        }
    }

    public byte[] GuestCar(int player)
    {
        lock (_gate) return _cars[player];
    }

    public void AnnounceStart()
    {
        lock (_gate) _startingId = (byte)(_setupId + 1);
    }

    public bool HostStarted
    {
        get
        {
            lock (_gate) return _hostStarted;
        }
    }

    public void PollHostStart()
    {
        byte id;
        lock (_gate) id = (byte)(_setupId + 1);
        SendTo(_hostEndPoint, [SetupReq, id]);
    }

    public void PublishSetup(byte[] setup)
    {
        lock (_gate)
        {
            _setupId++;
            _setup = setup;
            Array.Clear(_cars);
        }
    }

    public void SendCar(byte[] car)
    {
        if (LocalPlayer < 0) return;
        var packet = new byte[3 + car.Length];
        packet[0] = Car;
        lock (_gate) packet[1] = _setupId;
        packet[2] = (byte)LocalPlayer;
        Array.Copy(car, 0, packet, 3, car.Length);
        SendTo(_hostEndPoint, packet);
    }

    public void BeginSetup()
    {
        lock (_gate)
        {
            _inRace = true;
            _lobbyReady = false;
            _setupId++;
            _hostStarted = false;
            _setup = null;
            _setupParts = null;
            _setupHave = 0;
        }
    }

    public void RequestSetup()
    {
        byte id;
        lock (_gate) id = _setupId;
        SendTo(_hostEndPoint, [SetupReq, id]);
    }

    public byte[] ReceivedSetup
    {
        get
        {
            lock (_gate) return _setup;
        }
    }

    private void Broadcast(byte[] packet, IPEndPoint except)
    {
        if (!IsHost)
        {
            SendTo(_hostEndPoint, packet);
            return;
        }

        IPEndPoint[] guests;
        lock (_gate) guests = _guests.ConvertAll(g => g.EndPoint).ToArray();
        foreach (var g in guests)
            if (!g.Equals(except))
                SendTo(g, packet);
    }

    private void SendTo(IPEndPoint to, byte[] packet)
    {
        try
        {
            _udp.Send(packet, packet.Length, to);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"[netplay] send to {to} failed: {ex.Message}");
        }
    }

    private void JoinLoop()
    {
        while (!_closed)
        {
            bool ready;
            lock (_gate) ready = _lobbyReady;
            try { SendTo(_hostEndPoint, [Join, ready ? (byte)1 : (byte)0]); } catch (ObjectDisposedException) { return; }
            Thread.Sleep(500);
        }
    }

    private void ReceiveLoop()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            byte[] data;
            try
            {
                data = _udp.Receive(ref from);
            }
            catch (SocketException)
            {
                if (_closed) return;
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (data.Length == 0) continue;
            switch (data[0])
            {
                case Join when IsHost:
                    OnJoin(from, data.Length >= 2 && data[1] != 0);
                    break;
                case Welcome when !IsHost && data.Length >= 3:
                    OnWelcome(data);
                    break;
                case PauseMenu when !IsHost && data.Length >= 4:
                    lock (_gate)
                        if (data[1] == _race && _inRace && data[3] != _pickSeq)
                        {
                            _pickSeq = data[3];
                            _pausePick = data[2];
                        }

                    break;
                case Busy when !IsHost:
                    lock (_gate) _busyAt = Environment.TickCount64;
                    break;
                case State when data.Length >= 14:
                    OnState(data, from);
                    break;
                case Ready when data.Length >= 3:
                    OnReady(data, from);
                    break;
                case SetupReq when IsHost && data.Length >= 2 && IsGuest(from):
                    OnSetupRequest(data[1], from);
                    break;
                case Setup when !IsHost && data.Length >= 9:
                    OnSetup(data);
                    break;
                case Starting when !IsHost && data.Length >= 2:
                    lock (_gate)
                        if (data[1] == (byte)(_setupId + 1))
                            _hostStarted = true;
                    break;
                case Car when IsHost && data.Length >= 3:
                    lock (_gate)
                        // picks for the race that is loading on the host right now, its setup id comes next
                        if (data[1] == (byte)(_setupId + 1) && data[2] > 0 && data[2] < MaxPlayers)
                            _cars[data[2]] = data[3..];
                    break;
            }
        }
    }

    private bool IsGuest(IPEndPoint from)
    {
        lock (_gate) return _guests.FindIndex(g => g.EndPoint.Equals(from)) >= 0;
    }

    private void OnJoin(IPEndPoint from, bool ready)
    {
        int player;
        byte setup;
        lock (_gate)
        {
            var index = _guests.FindIndex(g => g.EndPoint.Equals(from));
            if (index < 0)
            {
                // a race is running, or the lobby is full
                if (_inRace)
                {
                    SendTo(from, [Busy]);
                    return;
                }

                if (_guests.Count >= MaxPlayers - 1) return;
                _guests.Add(new Guest { EndPoint = new IPEndPoint(from.Address, from.Port) });
                index = _guests.Count - 1;
                Console.WriteLine($"[netplay] {from} joined as player {index + 2}");
            }

            var guest = _guests[index];
            guest.LastSeen = Environment.TickCount64;
            if (!_inRace) guest.Ready = ready;
            player = index + 1;
            setup = _setupId;
        }

        SendTo(from, [Welcome, (byte)player, setup]);
    }

    private void OnWelcome(byte[] data)
    {
        lock (_gate)
        {
            // the player number only changes in the lobby, someone may have left?
            if (_inRace) return;
            var first = LocalPlayer < 0;
            if (LocalPlayer != data[1]) Console.WriteLine($"[netplay] {(first ? "joined" : "now")} as player {data[1] + 1}");
            LocalPlayer = data[1];

            // count races from the host's, so a late guest asks for the right one
            if (first) _setupId = data[2];
        }
    }

    private void OnState(byte[] data, IPEndPoint from)
    {
        if (IsHost) Broadcast(data, from);

        var player = data[2];
        var car = data[3];
        if (player >= Players || player == LocalPlayer || car >= MaxCars) return;
        var seq = BitConverter.ToUInt32(data, 4);
        var count = Math.Min(BitConverter.ToUInt16(data, 12), (data.Length - 14) / 4);
        lock (_gate)
        {
            if (data[1] != _race) return;

            // a player sending states is past the start, which counts as ready
            _ready |= 1 << player;
            if (seq > _inputSeq[player])
            {
                _inputSeq[player] = seq;
                _inputs[player] = BitConverter.ToUInt32(data, 8);
            }

            // older than what we have, UDP can reorder
            if (seq <= _stateSeq[car]) return;
            _stateSeq[car] = seq;
            if (_states[car]?.Length != count) _states[car] = new uint[count];
            for (var i = 0; i < count; i++) _states[car][i] = BitConverter.ToUInt32(data, 14 + i * 4);
            _stateFresh[car] = true;
        }
    }

    private void OnReady(byte[] data, IPEndPoint from)
    {
        if (IsHost) Broadcast(data, from);
        lock (_gate)
            if (data[1] == _race && data[2] < Players)
                _ready |= 1 << data[2];
    }

    private void OnSetupRequest(byte id, IPEndPoint from)
    {
        byte[] setup;
        byte players;
        lock (_gate)
        {
            if (id == _startingId && id != _setupId)
            {
                SendTo(from, [Starting, id]);
                return;
            }

            if (id != _setupId || _setup == null) return;
            setup = _setup;
            players = (byte)Players;
        }

        for (var offset = 0; offset < setup.Length; offset += SetupChunk)
        {
            var length = Math.Min(SetupChunk, setup.Length - offset);
            var packet = new byte[9 + length];
            packet[0] = Setup;
            packet[1] = id;
            packet[2] = players;
            BitConverter.TryWriteBytes(packet.AsSpan(3), (ushort)setup.Length);
            BitConverter.TryWriteBytes(packet.AsSpan(5), (ushort)offset);
            BitConverter.TryWriteBytes(packet.AsSpan(7), (ushort)length);
            Array.Copy(setup, offset, packet, 9, length);
            SendTo(from, packet);
        }
    }

    private void OnSetup(byte[] data)
    {
        var total = BitConverter.ToUInt16(data, 3);
        var offset = BitConverter.ToUInt16(data, 5);
        var length = Math.Min(BitConverter.ToUInt16(data, 7), data.Length - 9);
        lock (_gate)
        {
            if (data[1] == (byte)(_setupId + 1)) _hostStarted = true;
            if (data[1] != _setupId || _setup != null || offset + length > total) return;
            Players = Math.Clamp((int)data[2], 1, MaxPlayers);
            if (_setupParts?.Length != total)
            {
                _setupParts = new byte[total];
                _setupHave = 0;
            }

            Array.Copy(data, 9, _setupParts, offset, length);
            _setupHave |= 1 << (offset / SetupChunk);
            var chunks = (total + SetupChunk - 1) / SetupChunk;
            if (_setupHave == (1 << chunks) - 1) _setup = _setupParts;
        }
    }

    private static (IPAddress, int) ParseAddress(string address)
    {
        const int defaultPort = 7604;
        if (string.IsNullOrWhiteSpace(address)) return (IPAddress.Loopback, defaultPort);
        var parts = address.Split(':');
        var ip = IPAddress.TryParse(parts[0], out var parsed) ? parsed
            : parts[0].Length == 0 ? IPAddress.Loopback
            : Dns.GetHostAddresses(parts[0])[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort;
        return (ip, port);
    }
}
