using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

namespace UnlockTracks;

public sealed class UnlockTracksMod : IMod
{
    private const uint TrackAvailable = 0x80115D10;
    private const int TrackSlots = 16;

    public void OnLoad() => Event.AddListener<VSyncEvent>(OnVSync);

    public void OnUnload() => Event.RemoveListener<VSyncEvent>(OnVSync);

    private static void OnVSync(VSyncEvent e)
    {
        for (uint track = 0; track < TrackSlots; track++)
        {
            var flag = TrackAvailable + track * 4;
            if (e.Memory.ReadU32(flag) != 1) e.Memory.WriteU32(flag, 1);
        }
    }
}
