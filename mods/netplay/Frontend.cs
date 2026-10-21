using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using RecompOne.Runtime.Hardware;
using Recompiled;

namespace Netplay;

public static class Frontend
{
    // scratch space for the new menu, items and strings: Input_Update's original MIPS code, which the
    // recompiled game never reads and the front end overlay doesn't load over
    private const uint ScratchStart = 0x8009D000;
    private const uint ScratchEnd = 0x8009DC00;

    // menus (tMenuNFS4) keep up to 16 item pointers inline from +0x10, null terminated, and the menu Back
    // returns to at +0x54. items start with flags (bit 0 = disabled) and a text id at +4; go-to buttons
    // open the menu at +0x14 when confirmed
    private const uint MenuSize = 0xD4;
    private const uint MenuSelected = 0x08;
    private const uint MenuItems = 0x10;
    private const uint MenuParent = 0x54;
    private const uint MenuItemCount = 0x78;

    // Initialize gives every item this flag when the menu has it
    private const uint FlagInherited = 0x200;

    private const uint ItemText = 0x04;
    private const uint ItemTarget = 0x14;
    private const uint ItemCallback = 0x1C;
    private const uint ItemSize = 0x2C;

    // text ids of the Game Setup menu: [One Player, Two Player, Game Options, Trophy Room]
    private static readonly uint[] GameSetupTexts = [92, 93, 94, 95];

    // TextSys_Word ids past the game's own, answered with our strings
    private const uint TextBase = 0x7F00;
    private static readonly string[] Texts = ["ONLINE", "HOST GAME", "JOIN GAME", "WAITING FOR HOST", "NO PLAYERS JOINED", "PLAYERS READY 0/0 ", "RACE IN PROGRESS"];
    private const uint TextMultiplayer = TextBase, TextHost = TextBase + 1, TextJoin = TextBase + 2, TextWaiting = TextBase + 3,
        TextNoPlayers = TextBase + 4, TextReady = TextBase + 5, TextBusy = TextBase + 6;
    private static readonly uint[] _textAt = new uint[Texts.Length];

    private const uint StandInWord = 93; // "TWO PLAYER"

    private static uint _scratch;
    private static uint _joinCommand;

    private const uint DialogsPointer = 0x800519A8;
    private const uint MessageDialog = 0x2C;
    private const uint DialogText = 0x90;

    private static bool _waitingForHost;
    private static long _lastPoll;
    private static uint _host, _join;

    private static IMemory _memory;
    private static uint _gameSetup, _entry, _multiplayer;
    private static uint _multiplayerTitle, _hostText;

    [PreHook("front", "Initialize__9tMenuNFS4")]
    private static void BeforeMenuInitialize(CpuContext c, IMemory m)
    {
        var menu = c.A0;
        if (!IsGameSetup(m, menu)) return;

        Build(m, menu); // rebuilt every time
    }

    [PreHook("front", "Draw__9tMenuNFS4")]
    private static void BeforeMenuDraw(CpuContext c, IMemory m)
    {
        var menu = c.A0;
        var slot = menu + MenuItems + (uint)GameSetupTexts.Length * 4;
        if ((_entry != 0 && m.ReadU32(slot) == _entry) || !IsGameSetup(m, menu)) return;

        Build(m, menu);
        if ((m.ReadU32(menu) & FlagInherited) != 0) m.WriteU32(_entry, m.ReadU32(_entry) | FlagInherited);
        m.WriteU8(menu + MenuItemCount, (byte)(GameSetupTexts.Length + 1));
    }

    public static void Remove()
    {
        var m = _memory;
        if (m == null || _entry == 0) return;

        var slot = _gameSetup + MenuItems + (uint)GameSetupTexts.Length * 4;
        if (IsGameSetup(m, _gameSetup) && m.ReadU32(slot) == _entry)
        {
            if (m.ReadU32(_gameSetup + MenuSelected) >= GameSetupTexts.Length)
                m.WriteU32(_gameSetup + MenuSelected, (uint)GameSetupTexts.Length - 1);
            m.WriteU32(slot, 0);
            m.WriteU8(_gameSetup + MenuItemCount, (byte)GameSetupTexts.Length);
        }

        m.WriteU32(_multiplayer, _multiplayerTitle);
        m.WriteU32(_host + ItemText, _hostText);
        m.WriteU32(_join + ItemText, StandInWord);
        m.WriteU32(_join + ItemTarget, _gameSetup);
        m.WriteU32(_entry + ItemText, StandInWord);
        _entry = 0;
    }

    [PreHook("front", "ProcessInput__23tMenuItemGoToMenuButton7tPlayerR13tInputKeyTypeR12tMenuCommand")]
    private static void BeforeButtonInput(CpuContext c, IMemory m)
    {
        // A2 points at the key, 2 is confirm
        if (m.ReadU32(c.A2) != 2) return;
        if (c.A0 == _host && _host != 0)
        {
            Console.WriteLine("[netplay] host game selected");
            NetplayMod.StartSession(host: true);
        }
        else if (c.A0 == _join && _join != 0)
        {
            Console.WriteLine("[netplay] join game selected");
            NetplayMod.StartSession(host: false);
            // A3 is the command the menu acts on once the button is done
            _joinCommand = c.A3;
        }
    }

    // runs Test Drive's menu callback, then the one that opens car select
    [PostHook("front", "ProcessInput__23tMenuItemGoToMenuButton7tPlayerR13tInputKeyTypeR12tMenuCommand")]
    private static void AfterButtonInput(CpuContext c, IMemory m)
    {
        if (_joinCommand == 0) return;
        var command = _joinCommand;
        _joinCommand = 0;

        var (v0, v1, a0, a1, a2, a3) = (c.V0, c.V1, c.A0, c.A1, c.A2, c.A3);
        c.A0 = command;
        nfs4_front.MenuExtended_SetTestDrive__FR12tMenuCommand(c, m);
        c.A0 = command;
        nfs4_front.MenuExtended_GoToCarSelect__FR12tMenuCommand(c, m);
        (c.V0, c.V1, c.A0, c.A1, c.A2, c.A3) = (v0, v1, a0, a1, a2, a3);
    }

    [Replace("nfs4", "TextSys_Word__Fi")]
    private static void TextWord(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var index = c.A0 - TextBase;
        if (index < Texts.Length && _textAt[index] != 0)
        {
            c.V0 = _textAt[index];
            return;
        }

        orig(c, m);
    }

    // the rest of a word's table entry (flags, sizes) is read by these. our ids are past the end of
    // the table, so they get the entry of a game word of similar length
    [Replace("nfs4", "TextSys_WordFlags__Fi")]
    private static void TextWordFlags(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m) => AsGameWord(orig, c, m);

    [Replace("nfs4", "func_800B9F58")]
    private static void TextWordSize1(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m) => AsGameWord(orig, c, m);

    [Replace("nfs4", "func_800B9F78")]
    private static void TextWordSize2(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m) => AsGameWord(orig, c, m);

    private static void AsGameWord(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (c.A0 - TextBase < Texts.Length) c.A0 = StandInWord;
        orig(c, m);
    }

    [Replace("front", "MenuExtended_GoToRace__FR12tMenuCommand")]
    private static void GoToRace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var session = NetplayMod.Session;
        if (session is { IsHost: true })
        {
            var (joined, ready) = session.Lobby();
            if (joined > 0 && ready == joined)
            {
                session.CloseLobby();
                Console.WriteLine($"[netplay] starting the race with {joined} guest(s)");
                orig(c, m);
                return;
            }

            if (joined == 0)
            {
                ShowMessage(c, m, TextNoPlayers);
            }
            else
            {
                WriteString(m, _textAt[TextReady - TextBase], $"PLAYERS READY {ready}/{joined}");
                ShowMessage(c, m, TextReady);
            }

            return;
        }

        if (session == null || session.HostStarted)
        {
            orig(c, m);
            return;
        }

        if (session.HostBusy)
        {
            ShowMessage(c, m, TextBusy);
            return;
        }

        ShowMessage(c, m, TextWaiting);
        _waitingForHost = true;
        session.SetLobbyReady(true);
        Console.WriteLine("[netplay] waiting for the host to start");
    }

    private static void ShowMessage(CpuContext c, IMemory m, uint text)
    {
        var command = c.A0;
        var dialog = m.ReadU32(DialogsPointer) + MessageDialog;
        m.WriteU32(dialog + DialogText, _textAt[text - TextBase]);
        c.A0 = dialog;
        nfs4_front.Display__11tDialogBase(c, m);
        m.WriteU32(command, 0);
    }

    [PreHook("front", "DrawAllDialogs__11tDialogBase")]
    private static void BeforeDrawDialogs(CpuContext c, IMemory m)
    {
        if (!_waitingForHost) return;
        var session = NetplayMod.Session;
        if (session is not { HostStarted: true })
        {
            var now = Environment.TickCount64;
            if (now - _lastPoll >= 250)
            {
                _lastPoll = now;
                session?.PollHostStart();
            }

            return;
        }

        _waitingForHost = false;
        var a0 = c.A0;
        c.A0 = m.ReadU32(DialogsPointer) + MessageDialog;
        nfs4_front.Hide__11tDialogBase(c, m);
        c.A0 = a0;
        Console.WriteLine("[netplay] the host started, starting the race");
        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(300);
            Controller.Forced |= Controller.Cross;
            await System.Threading.Tasks.Task.Delay(200);
            Controller.Forced &= unchecked((ushort)~Controller.Cross);
        });
    }

    private static void Build(IMemory m, uint gameSetup)
    {
        _scratch = ScratchStart;
        for (var i = 0; i < Texts.Length; i++)
        {
            _textAt[i] = Alloc((uint)Texts[i].Length + 1);
            WriteString(m, _textAt[i], Texts[i]);
        }

        // One Player -> Single Race: the race menus everyone continues into, and the template for ours
        var onePlayer = m.ReadU32(m.ReadU32(gameSetup + MenuItems) + ItemTarget);
        var singleRace = m.ReadU32(m.ReadU32(onePlayer + MenuItems + 4) + ItemTarget);
        var template = m.ReadU32(gameSetup + MenuItems + 4);

        // HOST GAME / JOIN GAME, in a copy of the Single Race menu that goes back to Game Setup
        var multiplayer = Copy(m, singleRace, MenuSize);
        _multiplayerTitle = m.ReadU32(multiplayer);
        m.WriteU32(multiplayer, TextMultiplayer);
        m.WriteU32(multiplayer + MenuParent, gameSetup);
        // One Player's Test Drive entry: its callback sets up Test Drive, its menu is track select
        _host = Copy(m, m.ReadU32(onePlayer + MenuItems), ItemSize);
        _hostText = m.ReadU32(_host + ItemText);
        m.WriteU32(_host + ItemText, TextHost);
        _join = Button(m, template, TextJoin, 0);
        m.WriteU32(multiplayer + MenuItems, _host);
        m.WriteU32(multiplayer + MenuItems + 4, _join);
        m.WriteU32(multiplayer + MenuItems + 8, 0);

        var entry = Button(m, template, TextMultiplayer, multiplayer);
        var slot = (uint)GameSetupTexts.Length;
        m.WriteU32(gameSetup + MenuItems + slot * 4, entry);
        m.WriteU32(gameSetup + MenuItems + (slot + 1) * 4, 0);

        (_memory, _gameSetup, _entry, _multiplayer) = (m, gameSetup, entry, multiplayer);
    }

    private static uint Button(IMemory m, uint template, uint text, uint target)
    {
        var item = Copy(m, template, ItemSize);
        m.WriteU32(item, 0);
        m.WriteU32(item + ItemText, text);
        m.WriteU32(item + ItemTarget, target);
        m.WriteU32(item + ItemCallback, 0);
        return item;
    }

    private static uint Copy(IMemory m, uint from, uint size)
    {
        var to = Alloc(size);
        for (uint o = 0; o < size; o += 4) m.WriteU32(to + o, m.ReadU32(from + o));
        return to;
    }

    private static uint Alloc(uint size)
    {
        var at = _scratch;
        _scratch = (_scratch + size + 3) & ~3u;
        if (_scratch > ScratchEnd) throw new InvalidOperationException("netplay front end scratch area is full");
        return at;
    }

    private static bool IsGameSetup(IMemory m, uint menu)
    {
        for (var i = 0; i < GameSetupTexts.Length; i++)
        {
            var item = m.ReadU32(menu + MenuItems + (uint)i * 4);
            if (item == 0 || m.ReadU32(item + ItemText) != GameSetupTexts[i]) return false;
        }

        return true;
    }

    private static void WriteString(IMemory m, uint address, string text)
    {
        for (var i = 0; i < text.Length; i++) m.WriteU8(address + (uint)i, (byte)text[i]);
        m.WriteU8(address + (uint)text.Length, 0);
    }
}
