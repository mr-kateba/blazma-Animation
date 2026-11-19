#!/usr/bin/env python3
"""Windows PE checks for the release (runs on Linux, reads files only, loads nothing).

  pe-deps.py imports FILE...        the DLLs each file imports at load time
  pe-deps.py version FILE...        the file version (VS_FIXEDFILEINFO), e.g. 14.44.35211.0
  pe-deps.py check DIR              every .exe/.dll in DIR must find each import in DIR or among the Windows system DLLs
                                    (or a GPU driver DLL); prints the Visual C++ runtime DLLs the folder uses. Exit 1 when one
                                    is missing, e.g. MSVCP140.dll before the app-local runtime is copied in (DECISIONS D49).
"""
import os
import struct
import sys

# Part of every supported Windows (10/11 x64): never shipped. GPU driver DLLs come with the driver (a backend that needs a
# missing one simply does not load; the CPU path never needs them).
SYSTEM = {
    "kernel32.dll", "user32.dll", "advapi32.dll", "shell32.dll", "ole32.dll", "oleaut32.dll", "ws2_32.dll", "bcrypt.dll",
    "crypt32.dll", "gdi32.dll", "shlwapi.dll", "psapi.dll", "dbghelp.dll", "version.dll", "winmm.dll", "ntdll.dll",
    "setupapi.dll", "cfgmgr32.dll", "dxgi.dll", "d3d12.dll", "dxcore.dll", "ucrtbase.dll", "comdlg32.dll", "comctl32.dll",
    "rpcrt4.dll", "secur32.dll", "userenv.dll", "powrprof.dll", "iphlpapi.dll", "shcore.dll", "imm32.dll", "winhttp.dll",
}
DRIVER = {"vulkan-1.dll", "nvcuda.dll", "nvml.dll", "nvapi64.dll"}
VC_RUNTIME_PREFIXES = ("msvcp140", "vcruntime140", "vcomp140", "concrt140", "vccorlib140")


class Pe:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.b = f.read()
        b = self.b
        if b[:2] != b"MZ":
            raise ValueError(f"{path}: not a PE file")
        pe = struct.unpack_from("<I", b, 0x3C)[0]
        if b[pe:pe + 4] != b"PE\0\0":
            raise ValueError(f"{path}: no PE signature")
        self.machine, nsec = struct.unpack_from("<HH", b, pe + 4)
        optsize = struct.unpack_from("<H", b, pe + 20)[0]
        opt = pe + 24
        magic = struct.unpack_from("<H", b, opt)[0]
        self.plus = magic == 0x20B
        self.linker = (b[opt + 2], b[opt + 3])  # MajorLinkerVersion, MinorLinkerVersion: MSVC 14.44 links as 14.44
        dd = opt + (112 if self.plus else 96)
        self.dirs = [struct.unpack_from("<II", b, dd + 8 * i) for i in range(16)]
        self.sections = []
        sh = opt + optsize
        for i in range(nsec):
            name, vsize, va, rawsize, raw = struct.unpack_from("<8sIIII", b, sh + 40 * i)
            self.sections.append((va, max(vsize, rawsize), raw))

    def off(self, rva):
        for va, size, raw in self.sections:
            if va <= rva < va + size:
                return raw + rva - va
        raise ValueError(f"RVA {rva:#x} outside the sections")

    def cstr(self, rva):
        o = self.off(rva)
        return self.b[o:self.b.index(b"\0", o)].decode("ascii", "replace")

    def imports(self):
        rva = self.dirs[1][0]
        out = []
        while rva:
            oft, _, _, name, ft = struct.unpack_from("<IIIII", self.b, self.off(rva))
            if not (oft or name or ft):
                break
            out.append(self.cstr(name))
            rva += 20
        return out

    def version(self):
        # VS_FIXEDFILEINFO starts with the signature 0xFEEF04BD; the file version is two DWORDs after the struct version.
        i = self.b.find(struct.pack("<I", 0xFEEF04BD))
        if i < 0:
            return None
        ms, ls = struct.unpack_from("<II", self.b, i + 8)
        return f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"


def check(folder):
    files = sorted(f for f in os.listdir(folder) if f.lower().endswith((".exe", ".dll")))
    present = {f.lower() for f in files}
    missing, used = [], set()
    for f in files:
        pe = Pe(os.path.join(folder, f))
        if pe.machine != 0x8664:
            missing.append(f"{f} is not an x64 file (machine {pe.machine:#x})")
        for dll in pe.imports():
            low = dll.lower()
            if low.startswith(VC_RUNTIME_PREFIXES):
                used.add(low)
            if low in present or low in SYSTEM or low in DRIVER or low.startswith(("api-ms-win-", "ext-ms-")):
                continue
            missing.append(f"{f} imports {dll}, which is neither in {folder} nor a Windows system DLL")
    # The app-local runtime must be at least as new as the compiler that built the other files (an older msvcp140.dll can lack
    # functions they import, or crash them).
    if used and "msvcp140.dll" in present:
        crt = next(f for f in files if f.lower() == "msvcp140.dll")
        have = tuple(int(x) for x in (Pe(os.path.join(folder, crt)).version() or "0.0").split(".")[:2])
        for f in files:
            if f.lower().startswith(VC_RUNTIME_PREFIXES):
                continue
            pe = Pe(os.path.join(folder, f))
            if pe.linker[0] == 14 and pe.linker > have:
                missing.append(f"{f} was linked by MSVC {pe.linker[0]}.{pe.linker[1]}, newer than the runtime {have[0]}.{have[1]} in {folder}")
        print(f"Visual C++ runtime in the folder: {'.'.join(map(str, have))}")
    print("Visual C++ runtime DLLs used: " + (", ".join(sorted(used)) or "none"))
    for m in missing:
        print("MISSING: " + m)
    return 1 if missing else 0


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    cmd, args = argv[0], argv[1:]
    if cmd == "imports":
        for a in args:
            print(f"{os.path.basename(a)}: {' '.join(Pe(a).imports())}")
        return 0
    if cmd == "version":
        for a in args:
            print(f"{os.path.basename(a)} {Pe(a).version() or '?'}")
        return 0
    if cmd == "check":
        return check(args[0])
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
