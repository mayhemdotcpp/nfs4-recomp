using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;

namespace Netplay;

public sealed class NetplayMod : IMod
{
    private const uint SetupFlagHuman = 0x1;
    private const uint SetupFlagOpponent = 0x2;

    private const uint CarSetup = 0x288;
    private const uint CarHumanIndex = 0x258;

    private const uint GpCarCount = 0x2AC;
    private const uint GpHumanCount = 0x2B8;

    private const uint AllCars = 0x80110CA0;
    private const uint HumanCars = 0x80110D0C;

    private const uint CameraSlots = 0x80110570;
    private const uint CarWrongWay = 0x3F0;
    private const uint WrongWayRepeat = 0x8013EC1C;
    private const uint TrackSlices = 0x8013DAB4;

    private const uint GameSetup = 0x801144A4;
    private const uint GameSetupCarCount = GameSetup + 0x3C4;
    private const uint GameSetupCarRecords = 0x3D4;
    private const uint CarRecordSize = 0xB4;
    private const uint CarRecordRole = 0x04;

    private const uint SetupRaceType = 0x004;
    private const uint RaceTypeRace = 2;
    private const uint SetupTestDriveFlag = 0x018;
    private const uint SetupHasOpponents = 0x0BC;
    private const uint SetupOpponentCount = 0x3CC;
    private const uint CarRecordGrid = 0x54;
    private const uint GameSetupSize = GameSetupCarRecords + 9 * CarRecordSize;
    private const uint GameSetupLocalHuman = GameSetup + 0x1C;
    private const int SetupTimeoutMs = 180000;
    private const uint LocalPadResults = 0x8013E4FC;

    private const uint GpRacerCount = 0x2B0;
    private const uint CarIndex = 0x254;
    private const uint TrackSliceCount = 0x8013DABC;
    private const uint SetupReverse = 0x030;
    private const int GridRowSlices = 3;

    private const uint GpReplayMode = 0xE9C;
    private const uint SimState = 0x8011F37C;
    private const uint RampHumanCount = 0x8013DAF4;
    private const uint FetchedInput = 0x8013E504;

    // the parts of a car state that change while racing
    private static readonly (uint Start, uint End)[] StateRanges =
    [
        (0x004, 0x080), (0x090, 0x094), (0x098, 0x0B8), (0x0C0, 0x0D8), (0x0F0, 0x120), (0x12C, 0x130),
        (0x144, 0x190), (0x1A0, 0x1AC), (0x1B4, 0x22C), (0x234, 0x238), (0x25C, 0x260), (0x268, 0x270),
        (0x27C, 0x280), (0x284, 0x288), (0x28C, 0x2B0), (0x2B4, 0x2E0), (0x2E4, 0x30C), (0x314, 0x33C),
        (0x344, 0x34C), (0x350, 0x35C), (0x378, 0x37C), (0x388, 0x38C), (0x394, 0x3A0), (0x3F0, 0x410),
        (0x414, 0x430), (0x43C, 0x448), (0x44C, 0x458), (0x468, 0x470), (0x478, 0x4C4), (0x54C, 0x550),
        (0x554, 0x558), (0x564, 0x56C), (0x574, 0x578), (0x6C4, 0x6C8), (0x6EC, 0x6F8), (0x794, 0x7AC),
        (0x7B0, 0x7C4), (0x7C8, 0x7D8), (0x848, 0x85C), (0x864, 0x86C), (0x870, 0x878), (0x880, 0x884),
    ];

    private const int StartTimeoutMs = 60000;

    private static int _extraHumans = 1;
    private static bool _log;
    private static Session _session;

    private static readonly Dictionary<uint, uint> _converted = [];
    private static readonly Dictionary<uint, int> _slots = [];
    private static readonly uint[] _carOf = new uint[8];

    private static readonly uint[] _cars = new uint[Session.MaxCars];
    private static readonly int[] _owner = new int[Session.MaxCars];
    private static int _carCount;
    private static uint _originalLocalHuman = uint.MaxValue;
    private static uint? _raceSeed;

    private static readonly HashSet<uint> _logged = [];
    private static readonly Dictionary<int, uint> _lastInput = [];

    private static readonly uint[] _state = new uint[StateWords()];
    private static int _lastTick = int.MaxValue;
    private static bool _remoteAudioSource;
    private static uint _gp;
    private static IMemory _memory;

    public void OnLoad()
    {
        _log = Environment.GetEnvironmentVariable("NFS4_NETPLAY_LOG") == "1";

        var mode = Environment.GetEnvironmentVariable("NFS4_NET");
        if (mode is "host" or "join")
        {
            StartSession(mode == "host");
            return;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("NFS4_EXTRA_HUMANS"), out var n))
            _extraHumans = Math.Clamp(n, 0, 7);
        Console.WriteLine($"[netplay] local test, {_extraHumans} extra human car(s) on controller 2");
    }

    public void OnUnload()
    {
        _session?.Close();
        _session = null;
        if (_memory != null) ResetRace(_memory);
        Frontend.Remove();
    }

    public static Session Session => _session;

    // the address to join comes from NFS4_NET_ADDR (host[:port]), the local machine without it
    public static void StartSession(bool host)
    {
        if (_session != null)
        {
            if (_session.IsHost == host) return;
            Console.WriteLine($"[netplay] switching to {(host ? "hosting" : "joining")}");
            _session.Close();
            _session = null;
        }

        try
        {
            _session = new Session(host, Environment.GetEnvironmentVariable("NFS4_NET_ADDR"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[netplay] could not start the session: {ex.Message}");
        }
    }

    [PreHook("nfs4", "Cars_InitializeCarTablesFlagsAndCounters__FP8Car_tObj")]
    private static void BeforeCarTables(CpuContext c, IMemory m)
    {
        _gp = c.GP;
        _memory = m;
        if (m.ReadU32(c.GP + GpCarCount) == 0)
        {
            ResetRace(m);
            _lastTick = int.MaxValue;
            _carCount = 0;
        }

        var wanted = _session != null ? _session.Players - 1 : _extraHumans;
        if (_converted.Count >= wanted) return;

        var car = c.A0;
        var setup = m.ReadU32(car + CarSetup);
        var flags = m.ReadU32(setup + 4);
        if ((flags & SetupFlagOpponent) == 0 || (flags & SetupFlagHuman) != 0) return;

        _converted[setup] = flags;
        _slots[car] = _converted.Count;
        m.WriteU32(setup + 4, (flags & ~SetupFlagOpponent) | SetupFlagHuman);
        Console.WriteLine($"[netplay] car 0x{car:X8} is now extra player {_converted.Count}");
    }

    // during the race load (Nfs2_GameModuleStartUp), func_8009C7C8 builds GameSetup from what was picked
    // in the menus, and nothing has been loaded from it yet. the host's setup is the one everybody
    // races, with each guest's car in that guest's opponent slot: guests send their car, the host
    // waits for all of them before it publishes, guests replace their setup with the host's
    [PostHook("nfs4", "func_8009C7C8")]
    private static void BeforeRaceLoad(CpuContext c, IMemory m)
    {
        if (Environment.GetEnvironmentVariable("NFS4_NETPLAY_SETUPDUMP") is { Length: > 0 } dumpName)
            System.IO.File.WriteAllBytes(dumpName, ReadBytes(m, GameSetup, GameSetupSize));

        _raceSeed = null;
        if (_session == null) return;
        if (_session.IsHost)
        {
            HostRaceLoad(m);
            return;
        }

        _session.BeginSetup();
        var car = ReadBytes(m, CarRecord(0), CarRecordSize);
        var waited = Stopwatch.StartNew();
        var lastRequest = -1000L;
        while (_session.ReceivedSetup == null)
        {
            if (waited.ElapsedMilliseconds > SetupTimeoutMs)
            {
                Console.Error.WriteLine("[netplay] got no race setup from the host, racing with the local one");
                return;
            }

            if (waited.ElapsedMilliseconds - lastRequest >= 250)
            {
                lastRequest = waited.ElapsedMilliseconds;
                _session.SendCar(car);
                _session.RequestSetup();
            }

            Thread.Sleep(5);
        }

        var setup = _session.ReceivedSetup;
        var changed = 0;
        for (uint i = 0; i < GameSetupSize && i < setup.Length; i++)
        {
            if (m.ReadU8(GameSetup + i) != setup[i])
            {
                changed++;
                if (_log) Console.WriteLine($"[netplay] setup +0x{i:X3}: {m.ReadU8(GameSetup + i):X2} -> {setup[i]:X2}");
            }

            m.WriteU8(GameSetup + i, setup[i]);
        }

        if (setup.Length >= GameSetupSize + 4) _raceSeed = BitConverter.ToUInt32(setup, (int)GameSetupSize);
        Console.WriteLine($"[netplay] using the host's race setup, {changed} byte(s) differed (waited {waited.ElapsedMilliseconds} ms)");
    }

    private static void HostRaceLoad(IMemory m)
    {
        _session.AnnounceStart();
        _session.LockPlayers();
        var players = _session.Players;

        m.WriteU32(GameSetup + SetupRaceType, RaceTypeRace);
        m.WriteU32(GameSetup + SetupTestDriveFlag, 0);
        m.WriteU32(GameSetup + SetupHasOpponents, players > 1 ? 1u : 0u);
        m.WriteU32(GameSetup + SetupOpponentCount, (uint)(players - 1));
        m.WriteU32(GameSetupCarCount, (uint)players);
        m.WriteU32(CarRecord(0) + CarRecordGrid, (uint)players);

        var waited = Stopwatch.StartNew();
        for (var p = 1; p < players; p++)
        {
            byte[] car;
            while ((car = _session.GuestCar(p)) == null && waited.ElapsedMilliseconds < SetupTimeoutMs) Thread.Sleep(5);
            if (car == null)
            {
                Console.Error.WriteLine($"[netplay] player {p + 1} did not send a car, they get the host's");
                car = ReadBytes(m, CarRecord(0), CarRecordSize);
            }
            else
            {
                Console.WriteLine($"[netplay] player {p + 1} drives car model {car[0]}");
            }

            var record = CarRecord(p);
            for (uint i = 0; i < CarRecordSize && i < car.Length; i++) m.WriteU8(record + i, car[i]);
            m.WriteU32(record + CarRecordRole, SetupFlagOpponent);
            m.WriteU32(record + CarRecordGrid, (uint)p);
        }

        // the setup goes out with a random seed for the race after it
        _raceSeed = (uint)Environment.TickCount;
        _session.PublishSetup([.. ReadBytes(m, GameSetup, GameSetupSize), .. BitConverter.GetBytes(_raceSeed.Value)]);
        Console.WriteLine($"[netplay] race with {players} player(s), waited {waited.ElapsedMilliseconds} ms for their cars");
    }

    [PreHook("nfs4", "func_8007EFEC")]
    private static void BeforeAccidentRoll(CpuContext c, IMemory m)
    {
        if (_session == null || _raceSeed is not { } seed) return;
        var (a0, t1, t2) = (c.A0, c.T1, c.T2);
        (c.A0, c.T1) = (seed, 0x30);
        Dispatcher.Call(c, m, 0xA0);
        (c.A0, c.T1, c.T2) = (a0, t1, t2);
    }

    private static uint CarRecord(int car) => GameSetup + GameSetupCarRecords + (uint)car * CarRecordSize;

    private static byte[] ReadBytes(IMemory m, uint address, uint length)
    {
        var bytes = new byte[length];
        for (uint i = 0; i < length; i++) bytes[i] = m.ReadU8(address + i);
        return bytes;
    }

    // Camera_Init points the camera at humans[GameSetup+0x1C], so each machine sets that to
    // its own car while Camera_Init runs
    [PreHook("nfs4", "Camera_Init__Fv")]
    private static void BeforeCameraInit(CpuContext c, IMemory m)
    {
        if (_session == null || _session.LocalPlayer <= 0) return;
        foreach (var (car, slot) in _slots)
        {
            if (slot != _session.LocalPlayer) continue;
            _originalLocalHuman = m.ReadU32(GameSetupLocalHuman);
            m.WriteU32(GameSetupLocalHuman, m.ReadU32(car + CarHumanIndex));
        }
    }

    [PostHook("nfs4", "Camera_Init__Fv")]
    private static void AfterCameraInit(CpuContext c, IMemory m)
    {
        if (_originalLocalHuman == uint.MaxValue) return;
        m.WriteU32(GameSetupLocalHuman, _originalLocalHuman);
        _originalLocalHuman = uint.MaxValue;
    }

    [Replace("nfs4", "InGame_SetRamp__Fv")]
    private static void SetRamp(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var humans = m.ReadU32(RampHumanCount);
        if ((int)humans <= 2)
        {
            orig(c, m);
            return;
        }

        m.WriteU32(RampHumanCount, 2);
        orig(c, m);
        m.WriteU32(RampHumanCount, humans);
    }

    // netplay races get rows of two instead: the game's own two racer placement (side by side on the start
    // slice, lanes by grid position), then every row a few slices further back
    [Replace("nfs4", "Cars_CalculateStartingGridOffset__FP8Car_tObjPiP8coorddef")]
    private static void StartingGrid(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var racers = m.ReadU32(c.GP + GpRacerCount);
        if (_session == null || (int)racers < 3)
        {
            orig(c, m);
            return;
        }

        var car = c.A0;
        var slicePointer = c.A1;
        var gp = c.GP;
        m.WriteU32(gp + GpRacerCount, 2);
        orig(c, m);
        m.WriteU32(gp + GpRacerCount, racers);

        var grid = (int)m.ReadU32(CarRecord((int)m.ReadU32(car + CarIndex)) + CarRecordGrid);
        var row = Math.Max(grid - 1, 0) / 2;
        var direction = m.ReadU32(GameSetup + SetupReverse) != 0 ? 1 : -1;
        var slices = (int)m.ReadU32(TrackSliceCount);
        var slice = (int)m.ReadU32(slicePointer) + direction * GridRowSlices * row;
        if (slices > 0) slice = ((slice % slices) + slices) % slices;
        m.WriteU32(slicePointer, (uint)slice);
    }

    [Replace("nfs4", "DashHUD_HUDCalc__Fi")]
    private static void DashboardCalc(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m) => AsLocalHuman(orig, c, m);

    private static void AsLocalHuman(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (c.A0 == 0 && _session != null && _session.LocalPlayer > 0)
        {
            var car = _carOf[_session.LocalPlayer];
            if (car != 0) c.A0 = m.ReadU32(car + CarHumanIndex);
        }

        orig(c, m);
    }

    [Replace("nfs4", "func_80075F28")]
    private static void AudioSource(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var local = c.A0 == 0 ? LocalCar() : 0;
        if (local == 0)
        {
            orig(c, m);
            return;
        }

        _remoteAudioSource = true;
        orig(c, m);
        _remoteAudioSource = false;
        CountWrongWay(c, m, local);
    }

    [Replace("nfs4", "AudioCmn_PlayWrongWaySFX__Fv")]
    private static void WrongWaySound(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (!_remoteAudioSource) orig(c, m);
    }

    private static void CountWrongWay(CpuContext c, IMemory m, uint car)
    {
        if (m.ReadU32(GameSetup) is 1 or 5) return;

        if (m.ReadU32(car + 0x390) == 2 || m.ReadU32(car + 0x460) != 0 || m.ReadU32(car + 0x278) != 0 || !Backwards(m, car))
        {
            m.WriteU32(car + CarWrongWay, 0);
            return;
        }

        if (m.ReadU16(car + 0x17C) != 0 || m.ReadU32(car + 0x78C) != 0) return;

        var time = (int)m.ReadU32(car + CarWrongWay) + 1;
        m.WriteU32(car + CarWrongWay, (uint)time);
        if (time >= 64 && (time & 31) == 0 && (time < 148 || m.ReadU32(WrongWayRepeat) != 0))
            nfs4_nfs4.AudioCmn_PlayWrongWaySFX__Fv(c, m);
    }

    // the car's velocity along the track direction of its slice
    private static bool Backwards(IMemory m, uint car)
    {
        if ((m.ReadU32(car + 0x260) & 0x200) != 0) return m.ReadU32(car + 0x550) != m.ReadU32(car + 0x554);

        var slice = m.ReadU32(TrackSlices) + (uint)(short)m.ReadU16(car + 0x8) * 32;
        var along = FixedMult(m.ReadU32(car + 0x108), (sbyte)m.ReadU8(slice + 0xF))
                    + FixedMult(m.ReadU32(car + 0x10C), (sbyte)m.ReadU8(slice + 0x10))
                    + FixedMult(m.ReadU32(car + 0x110), (sbyte)m.ReadU8(slice + 0x11));
        if (m.ReadU32(GameSetup + SetupReverse) != 0) along = -along;
        return along < 0;
    }

    private static int FixedMult(uint a, int b) => (int)(((long)(int)a * b + 0x8000) >> 16);

    private static uint LocalCar()
    {
        if (_session == null || _session.LocalPlayer <= 0) return 0;
        foreach (var (car, slot) in _slots)
            if (slot == _session.LocalPlayer)
                return car;
        return 0;
    }

    [PostHook("nfs4", "Cars_CleanUp__Fv")]
    private static void AfterCarsCleanUp(CpuContext c, IMemory m)
    {
        _session?.EndRace();
        ResetRace(m);
    }

    // 1 continue, 2 restart, 4 and 5 quit
    // the host quitting or restarting does it for everybody
    [PostHook("nfs4", "MPause_Logic__Fv")]
    private static void AfterPauseMenu(CpuContext c, IMemory m)
    {
        if (_session == null || !_session.IsHost) return;
        if (c.V0 == 2)
        {
            Console.WriteLine("[netplay] restarted the race, restarting it for everybody");
            _session.SendPausePick(Session.PickRestart);
        }
        else if (c.V0 == 4 || c.V0 == 5)
        {
            Console.WriteLine("[netplay] quit the race, ending it for everybody");
            _session.SendPausePick(Session.PickQuit);
        }
    }

    private static void FollowPausePick(CpuContext c, IMemory m, byte pick)
    {
        var (v0, v1, a0, a1, a2, a3) = (c.V0, c.V1, c.A0, c.A1, c.A2, c.A3);
        if (pick == Session.PickRestart)
        {
            Console.WriteLine("[netplay] the host restarted the race");
            nfs4_nfs4.AudioCmn_UnPauseAndRestart__Fv(c, m);
            m.WriteU32(SimState + 0x8, 1);
            m.WriteU32(SimState + 0x4, 1);
            m.WriteU32(SimState, 1);
        }
        else if (pick == Session.PickQuit)
        {
            Console.WriteLine("[netplay] the host quit the race");
            nfs4_nfs4.Hud_BTC_QuitOut__Fv(c, m);
            nfs4_nfs4.AudioCmn_UnPauseAndRestart__Fv(c, m);
            m.WriteU32(SimState + 0x8, 0);
            m.WriteU32(SimState + 0x4, 1);
            m.WriteU32(SimState, 0);
            c.A0 = 1;
            nfs4_nfs4.func_800B92D8(c, m);
            m.WriteU32(c.GP + 0xED8, 1);
        }

        (c.V0, c.V1, c.A0, c.A1, c.A2, c.A3) = (v0, v1, a0, a1, a2, a3);
    }

    private static bool InReplay(CpuContext c, IMemory m) => (int)m.ReadU32(c.GP + GpReplayMode) >= 2;

    [PreHook("nfs4", "SimQueue_SetCurrentInput__Fi")]
    private static void BeforeTick(CpuContext c, IMemory m)
    {
        if (_session == null || InReplay(c, m)) return;
        var tick = (int)c.A0;
        if (!_session.IsHost && _session.TakePausePick() is var pick and not 0) FollowPausePick(c, m, pick);

        // a new race, or the same one restarted
        if (tick <= _lastTick)
        {
            _session.NewRace();
            FindCars(m);
            WaitForPlayers();
        }

        _lastTick = tick;

        var local = _session.LocalPlayer;
        if (_log && tick % 60 == 0)
        {
            var line = $"[netplay] tick {tick}:";
            for (var i = 0; i < _carCount; i++)
                line += $" car{i}/p{_owner[i] + 1}=({(int)m.ReadU32(_cars[i] + 0x10) >> 8},{(int)m.ReadU32(_cars[i] + 0x18) >> 8})";
            Console.WriteLine($"{line} camera=0x{m.ReadU32(CameraSlots):X8}");
        }

        for (var i = 0; i < _carCount; i++)
            if (_owner[i] != local && _session.TakeState(i, _state))
                WriteState(m, _cars[i], _state);

        if (local < 0) return;
        var input = m.ReadU32(LocalPadResults);
        for (var i = 0; i < _carCount; i++)
        {
            if (_owner[i] != local) continue;
            // AI and traffic every second tick, they only need to stay close
            var playerCar = Array.IndexOf(_carOf, _cars[i]) >= 0;
            if (!playerCar && (tick & 1) != 0) continue;
            ReadState(m, _cars[i], _state);
            _session.SendState(i, input, _state);
        }
    }

    private static void FindCars(IMemory m)
    {
        _carCount = (int)Math.Min(m.ReadU32(_gp + GpCarCount), (uint)Session.MaxCars);
        for (var i = 0; i < _carCount; i++)
        {
            _cars[i] = m.ReadU32(AllCars + (uint)i * 4);
            _owner[i] = 0;
        }

        Array.Clear(_carOf);
        var humans = Math.Min(m.ReadU32(_gp + GpHumanCount), 9u);
        for (uint i = 0; i < humans; i++)
        {
            var car = m.ReadU32(HumanCars + i * 4);
            var player = _slots.TryGetValue(car, out var slot) ? slot : 0;
            if (player < _carOf.Length) _carOf[player] = car;
            var index = Array.IndexOf(_cars, car, 0, _carCount);
            if (index >= 0) _owner[index] = player;
        }

        if (_log)
            for (var p = 0; p < _session.Players; p++)
                Console.WriteLine($"[netplay] player {p + 1}: car 0x{_carOf[p]:X8}{(p == _session.LocalPlayer ? " (you)" : "")}");
    }

    private static void WaitForPlayers()
    {
        var waited = Stopwatch.StartNew();
        var lastSend = -1000L;
        while (!_session.AllReady)
        {
            if (waited.ElapsedMilliseconds > StartTimeoutMs)
            {
                Console.Error.WriteLine("[netplay] the other players did not reach the race, starting without them");
                return;
            }

            if (waited.ElapsedMilliseconds - lastSend >= 100)
            {
                lastSend = waited.ElapsedMilliseconds;
                _session.SendReady();
            }

            Thread.Sleep(1);
        }

        _session.SendReady();
        Console.WriteLine($"[netplay] all players ready after {waited.ElapsedMilliseconds} ms");
    }

    [Replace("nfs4", "Replay_DoReplay__FP8Car_tObj")]
    private static void DoReplay(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var car = c.A0;
        if (InReplay(c, m))
        {
            // nothing was recorded for the extra humans (their replay buffers don't exist), and
            // playing back past the two that do would write over other data
            if (_slots.ContainsKey(car)) m.WriteU32(FetchedInput, 0);
            else orig(c, m);
            return;
        }

        // human cars get their input here instead of from orig, which would also record it for the replay:
        // the replay buffers only have room for two humans
        int player;
        if (_slots.TryGetValue(car, out var slot)) player = slot;
        else if (_session != null && m.ReadU32(car + CarHumanIndex) == 0) player = 0;
        else
        {
            orig(c, m);
            return;
        }

        if (_log && _logged.Add(car))
            Console.WriteLine($"[netplay] player {player + 1} drives car 0x{car:X8}, human index {(int)m.ReadU32(car + CarHumanIndex)}");

        var input = InputFor(player, m);
        if (_log && _session == null && _lastInput.GetValueOrDefault(player) != input)
            Console.WriteLine($"[netplay] extra player {player} input 0x{input:X8}");
        _lastInput[player] = input;
        m.WriteU32(FetchedInput, input);
    }

    private static uint InputFor(int player, IMemory m)
    {
        if (_session != null)
        {
            if (player == _session.LocalPlayer) return m.ReadU32(LocalPadResults);
            return player < _session.Players ? _session.LatestInput(player) : 0u;
        }

        // local test
        return player == 1 ? Encode(Controller.State2) : 0u;
    }

    private static uint Encode(ushort pad)
    {
        bool Down(ushort bit) => (pad & bit) == 0;

        uint steer = Down(Controller.Right) ? 0x7Fu : Down(Controller.Left) ? 0x81u : 0u;
        uint gas = Down(Controller.Cross) ? 0xFFu : 0u;
        uint brake = Down(Controller.Square) ? 0xFFu : 0u;

        uint buttons = 0;
        if (Down(Controller.Circle)) buttons |= 0x01;
        if (Down(Controller.Up)) buttons |= 0x02;
        if (Down(Controller.R1)) buttons |= 1 << 3;
        else if (Down(Controller.R2)) buttons |= 2 << 3;
        else if (Down(Controller.Select)) buttons |= 3 << 3;

        return steer | gas << 8 | brake << 16 | buttons << 24;
    }

    private static int StateWords()
    {
        var words = 0;
        foreach (var (start, end) in StateRanges) words += (int)(end - start) / 4;
        return words;
    }

    private static void ReadState(IMemory m, uint car, uint[] state)
    {
        var i = 0;
        foreach (var (start, end) in StateRanges)
            for (var o = start; o < end; o += 4)
                state[i++] = m.ReadU32(car + o);
    }

    private static void WriteState(IMemory m, uint car, uint[] state)
    {
        var i = 0;
        foreach (var (start, end) in StateRanges)
            for (var o = start; o < end; o += 4)
                m.WriteU32(car + o, state[i++]);
    }

    private static void ResetRace(IMemory m)
    {
        foreach (var (setup, flags) in _converted) m.WriteU32(setup + 4, flags);
        _converted.Clear();
        _slots.Clear();
        _logged.Clear();
    }
}
