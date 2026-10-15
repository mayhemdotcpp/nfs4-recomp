using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

namespace UnlockCars;

public sealed class UnlockCarsMod : IMod
{
    private const uint UnlockFlags = 0x80115734;
    private const uint UnlockFlagsCopy = 0x80115764;
    private const uint CarSlots = 48;

    public void OnLoad() => Event.AddListener<VSyncEvent>(OnVSync);

    public void OnUnload() => Event.RemoveListener<VSyncEvent>(OnVSync);

    private static void OnVSync(VSyncEvent e)
    {
        for (uint car = 0; car < CarSlots; car++)
        {
            if (e.Memory.ReadU8(UnlockFlags + car) != 1) e.Memory.WriteU8(UnlockFlags + car, 1);
            if (e.Memory.ReadU8(UnlockFlagsCopy + car) != 1) e.Memory.WriteU8(UnlockFlagsCopy + car, 1);
        }
    }
}
