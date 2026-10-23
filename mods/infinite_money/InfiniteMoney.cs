using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

namespace InfiniteMoney;

public sealed class InfiniteMoneyMod : IMod
{
    private const uint Money = 0x80115DA4;
    private const uint MaxAmount = 9_999_999;

    public void OnLoad() => Event.AddListener<VSyncEvent>(OnVSync);

    public void OnUnload() => Event.RemoveListener<VSyncEvent>(OnVSync);

    private static void OnVSync(VSyncEvent e)
    {
        if (e.Memory.ReadU32(Money) != MaxAmount) e.Memory.WriteU32(Money, MaxAmount);
    }
}
