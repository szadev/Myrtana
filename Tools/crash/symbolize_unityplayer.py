"""Symbolize UnityPlayer.dll frames from a Player.log crash stack (no cdb needed).

usage: python symbolize_unityplayer.py <variation> <base-hex> <image-size> <addr-hex>...
  variation : win64_player_nondevelopment_il2cpp | win64_player_nondevelopment_mono | ...
  base/size : from the Player.log module line "UnityPlayer.dll (BASE), size: SIZE"
The pdb comes from the editor's windowsstandalonesupport/Variations/<variation> folder (same Unity version).
"""
import ctypes, glob, os, sys
from ctypes import wintypes as W

EDITOR = r"D:\Program Files\Unity\Hub\Editor\6000.6.5f1\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations"
DBGHELP = r"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbghelp.dll"

class SYMBOL_INFOW(ctypes.Structure):
    _fields_ = [("SizeOfStruct", W.ULONG), ("TypeIndex", W.ULONG), ("Reserved", ctypes.c_uint64 * 2), ("Index", W.ULONG),
                ("Size", W.ULONG), ("ModBase", ctypes.c_uint64), ("Flags", W.ULONG), ("Value", ctypes.c_uint64),
                ("Address", ctypes.c_uint64), ("Register", W.ULONG), ("Scope", W.ULONG), ("Tag", W.ULONG),
                ("NameLen", W.ULONG), ("MaxNameLen", W.ULONG), ("Name", ctypes.c_wchar * 1024)]

def main(a):
    var, base, size = a[0], int(a[1], 16), int(a[2])
    folder = os.path.join(EDITOR, var)
    pdb = glob.glob(os.path.join(folder, "UnityPlayer*.pdb"))[0]
    dbg = ctypes.WinDLL(DBGHELP)
    h = W.HANDLE(0x1234)
    dbg.SymSetOptions(0x2 | 0x4 | 0x40)
    dbg.SymInitializeW.argtypes = [W.HANDLE, W.LPCWSTR, W.BOOL]
    dbg.SymInitializeW(h, folder, False)
    dbg.SymLoadModuleExW.restype = ctypes.c_uint64
    dbg.SymLoadModuleExW.argtypes = [W.HANDLE, W.HANDLE, W.LPCWSTR, W.LPCWSTR, ctypes.c_uint64, W.DWORD, ctypes.c_void_p, W.DWORD]
    dbg.SymLoadModuleExW(h, None, pdb, None, base, size, None, 0)
    dbg.SymFromAddrW.argtypes = [W.HANDLE, ctypes.c_uint64, ctypes.POINTER(ctypes.c_uint64), ctypes.POINTER(SYMBOL_INFOW)]
    for s_ in a[3:]:
        addr = int(s_, 16)
        si = SYMBOL_INFOW(); si.SizeOfStruct = 88; si.MaxNameLen = 1023; d = ctypes.c_uint64()
        ok = dbg.SymFromAddrW(h, addr, ctypes.byref(d), ctypes.byref(si))
        print(f"{addr:#x} rva {addr - base:#x} " + (f"{si.Name}+{d.value:#x}" if ok else f"? (err {ctypes.GetLastError()})"))

if __name__ == "__main__":
    main(sys.argv[1:])
