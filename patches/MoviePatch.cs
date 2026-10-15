using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;

namespace Nfs4Port.Patches;

public static class MoviePatch
{
    // the movie player (func_8004D8BC) polls StGetNext up to 50000 times and treats running
    // out as the end of the movie. On a PS1 that spin lasts long enough for the next frame to
    // come off the disc. The recompiled version runs this loop too quickly, causing the intro 
    // movie to be skipped. Add a delay to each empty poll.
    public static void StGetNext(CpuContext c, IMemory m)
    {
        var a0 = c.A0;
        var a1 = c.A1;
        LibCdStream.StGetNext(c, m);
        if (c.V0 == 0) return;

        Thread.Sleep(1);
        c.A0 = a0;
        c.A1 = a1;
    }
}
