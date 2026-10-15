import pathlib
import sys

GEN = pathlib.Path(__file__).resolve().parent.parent / "generated" / "nfs4.cs"
W = "Nfs4Port.Enhancements.Widescreen"


def function_span(lines, addr):
    # functions are found by address through the dispatch table, so their names (sweep or decomp) don't matter
    entry = next(l for l in lines if f"[0x{addr:08X}u] = nfs4_nfs4." in l)
    name = entry.split("nfs4_nfs4.", 1)[1].rstrip(",").strip()
    start = next(i for i, l in enumerate(lines) if l.strip().startswith(f"public static void {name}("))
    end = next(i for i in range(start, len(lines)) if lines[i] == "    }")
    return start, end


def edit(lines, addr, old, new, expected):
    start, end = function_span(lines, addr)
    name = f"0x{addr:08X}"
    body = "\n".join(lines[start:end + 1])
    if new in body:
        return f"{name}: already applied"
    found = body.count(old)
    if found != expected:
        sys.exit(f"{name}: expected {expected} x '{old}', found {found}. Generated code changed, check the edit.")
    lines[start:end + 1] = body.replace(old, new).split("\n")
    return f"{name}: {found} x '{old}'"


def main():
    lines = GEN.read_text(encoding="utf-8").split("\n")
    log = []
    # widescreen 16:9 (0x80085C80): the camera matrix horizontal scale factor
    log.append(edit(lines, 0x80085A2C, "0xDC00u;", f"({W} ? 0xD200u : 0xDC00u);", 1))
    
    # widescreen 16:9 (0x80085CC4..0x80085D08): apply that scale to the X row of the matrix instead of the Y row
    for y, x in (("0x18u", "0x0Cu"), ("0x1Cu", "0x10u"), ("0x20u", "0x14u")):
        log.append(edit(lines, 0x80085A2C, f"(c.S3 + {y})", f"(c.S3 + ({W} ? {x} : {y}))", 4))
        
    # widescreen 16:9: GeomScreen 190 -> 170 to undo the slight vertical stretch (delay slots are emitted twice)
    for addr, n in ((0x8008275C, 2), (0x800836B8, 1), (0x80083CD4, 4), (0x8008427C, 1),
                    (0x8008602C, 2), (0x80086240, 2)):
        log.append(edit(lines, addr, "0x000000BEu;", f"({W} ? 0x000000AAu : 0x000000BEu);", n))
        
    # widescreen 16:9 (0x800D17A0, 0x800D17C8): move the sky so the wider view has no voids
    log.append(edit(lines, 0x800D1798, "c.S0 = 0x00000078u;", f"c.S0 = ({W} ? 0x00000084u : 0x00000078u);", 1))
    log.append(edit(lines, 0x800D1798, "c.S0 = 0x0000003Cu;", f"c.S0 = ({W} ? 0x00000044u : 0x0000003Cu);", 1))
    
    # disable sub-divide correction (PGXP textures fix) (0x800C6A04..) only while PGXP is on
    log.append(edit(lines, 0x800C6970, "(int)c.V0 < 320 ?",
                    "(int)c.V0 < (RecompOne.Runtime.Pgxp.Pgxp.Enabled ? 1344 : 320) ?", 4))
    
    GEN.write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(log))


if __name__ == "__main__":
    main()
