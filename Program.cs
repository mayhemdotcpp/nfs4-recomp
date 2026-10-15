using RecompOne.Runtime.Memory;

if (Environment.GetEnvironmentVariable("NFS4_TRACE") == "1")
{
    RecompOne.Runtime.Log.CdOn = true;
    RecompOne.Runtime.Log.SdkOn = true;
}

var input = Environment.GetEnvironmentVariable("NFS4_INPUT");
if (!string.IsNullOrEmpty(input)) ScriptedInput.Start(input);

if (Environment.GetEnvironmentVariable("NFS4_WIDE") != "0")
{
    Nfs4Port.Enhancements.Widescreen = true;
    RecompOne.Runtime.Hle.Display.SourceAspect = 16f / 9f;
    RecompOne.Runtime.Hle.Display.OutputAspect = 16f / 9f;
}

RecompOne.Runtime.Runtime.Defaults(view =>
{
    view.Default(RecompOne.Runtime.Pgxp.Pgxp.KeyEnable, true);
});

RecompOne.Runtime.Config.KeyBindings.Default = () => new()
{
    Cross = "W, Enter", Square = "S", Circle = "Space", Triangle = "C, Backspace",
    L1 = "Q", R1 = "E", L2 = "Z", R2 = "X", L3 = "F", R3 = "G",
    Start = "Escape", Select = "ShiftRight",
    Up = "Up", Down = "Down", Left = "A, Left", Right = "D, Right"
};

RecompOne.Runtime.Config.ConfigManager.Load();
var game = RecompOne.Runtime.Config.ConfigManager.Game;

if (string.IsNullOrWhiteSpace(game.CdPath) || !File.Exists(game.CdPath))
{
    var disc = FindDisc(Path.Combine(AppContext.BaseDirectory, "disc"));
    if (disc != null)
    {
        game.CdPath = disc;
        RecompOne.Runtime.Config.ConfigManager.SaveGame();
    }
}

using (var icon = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico"))
{
    if (icon != null)
    {
        using var bytes = new MemoryStream();
        icon.CopyTo(bytes);
        RecompOne.Runtime.Runtime.SetIcon(bytes.ToArray());
    }
}

var m = new PSMemory();
var cue = args.Length > 0 ? args[0] : null;
RecompOne.Runtime.Runtime.Run(() => Recompiled.Entry.Run(m, cue, "Need for Speed: High Stakes"));

static string FindDisc(string dir)
{
    if (!Directory.Exists(dir)) return null;
    foreach (var pattern in new[] { "*.chd", "*.cue" })
    {
        var found = Directory.GetFiles(dir, pattern);
        if (found.Length > 0) return found.Length == 1 ? found[0] : null;
    }

    return null;
}

static class ScriptedInput
{
    private static readonly Dictionary<string, int> Bits = new()
    {
        ["select"] = 0, ["l3"] = 1, ["r3"] = 2, ["start"] = 3, ["up"] = 4, ["right"] = 5, ["down"] = 6, ["left"] = 7,
        ["l2"] = 8, ["r2"] = 9, ["l1"] = 10, ["r1"] = 11, ["triangle"] = 12, ["circle"] = 13, ["cross"] = 14, ["square"] = 15,
    };

    public static void Start(string script)
    {
        var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s =>
        {
            var p = s.Split(':');
            var mask = p[1].Split('+').Aggregate(0, (acc, b) => acc | 1 << Bits[b.Trim().ToLowerInvariant()]);
            return (At: double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), Mask: (ushort)mask,
                HoldMs: p.Length > 2 ? int.Parse(p[2]) : 150);
        }).OrderBy(s => s.At).ToList();

        new Thread(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var step in steps)
            {
                while (clock.Elapsed.TotalSeconds < step.At) Thread.Sleep(5);
                Console.WriteLine($"[input] t={step.At}s mask=0x{step.Mask:X4} hold={step.HoldMs}ms");
                var until = clock.ElapsedMilliseconds + step.HoldMs;
                // the window thread rewrites the pad state every frame, so keep re-asserting the press
                while (clock.ElapsedMilliseconds < until)
                {
                    RecompOne.Runtime.Hardware.Controller.State = (ushort)(RecompOne.Runtime.Hardware.Controller.State & ~step.Mask);
                    Thread.Sleep(1);
                }
            }
        }) { IsBackground = true, Name = "scripted-input" }.Start();
    }
}
