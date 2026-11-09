using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace G3.Tools.SptBake
{
    /// <summary>
    /// Thin, pure-compute access to the SpeedTreeRT 4.0 code statically linked into Gothic 3 CP 1.75 <c>Engine.dll</c>
    /// (build stamp 0x4F959CA6). Only the calls the engine itself makes in <c>eCWraper_SpeedTreeRT::Read</c> and
    /// <c>eCSpeedTreeRenderBuffer::Create*Geometry</c> are used: allocate + construct, LoadTree, the lighting/wind
    /// setters, Compute, GetGeometry. No engine subsystem (file system, renderer, registry, saves) is touched.
    ///
    /// Calling conventions (all addresses absolute, DLL at its preferred base 0x10000000):
    /// <list type="bullet">
    /// <item>operator new          0x10304654  cdecl(size) → ptr</item>
    /// <item>CSpeedTreeRT ctor      0x102B5020  cdecl(this) → this; object size 0x9C</item>
    /// <item>LoadTree               0x102B60C0  ECX = buffer; stack (this, size); callee pops 8; AL = ok</item>
    /// <item>SetNumWindMatrices(4)  0x102B7340  no arguments (value inlined)</item>
    /// <item>Set{Branch,Frond}LightingMethod 0x102B6830 / 0x102B6A90  ECX = method; stack (this); pops 4</item>
    /// <item>SetLeafLightingMethod  0x102B6960  EDX = method; stack (this); pops 4</item>
    /// <item>Set{Branch,Leaf,Frond}WindMethod(GPU) 0x102B70E0 / 0x102B6FC0 / 0x102B7210, SetNumLeafRockingGroups(1)
    ///       0x102B6E90  stack (this); values inlined</item>
    /// <item>Compute                0x102B59B0  stack (this, const float* transform4x4); caller cleans</item>
    /// <item>GetGeometry            0x102B8D90  ECX = SGeometry*, EDX = this; stack (mask, branchLod, frondLod, leafLod);
    ///       pops 16. mask: 1 branches, 2 fronds, 4 leaves, 8 billboards; LOD −1 = current</item>
    /// <item>SGeometry ctor         0x102B4D50  EAX = SGeometry*; struct size 0x120</item>
    /// </list>
    /// </summary>
    public sealed unsafe class SpeedTreeRuntime : IDisposable
    {
        public const uint ExpectedBase = 0x10000000, ExpectedTimeStamp = 0x4F959CA6;

        const uint FnNew = 0x10304654, FnCtor = 0x102B5020, FnLoadTree = 0x102B60C0, FnSetNumWindMatrices = 0x102B7340,
                   FnBranchLighting = 0x102B6830, FnLeafLighting = 0x102B6960, FnFrondLighting = 0x102B6A90,
                   FnBranchWind = 0x102B70E0, FnLeafWind = 0x102B6FC0, FnFrondWind = 0x102B7210, FnLeafRockGroups = 0x102B6E90,
                   FnCompute = 0x102B59B0, FnGetGeometry = 0x102B8D90, FnGeometryCtor = 0x102B4D50;

        public const int TreeSize = 0x9C, GeometrySize = 0x120;

        readonly IntPtr _module;
        readonly NativeCall _call = new NativeCall();
        readonly delegate* unmanaged[Thiscall]<float*, float, void> _setToScaling;

        public SpeedTreeRuntime(string gameDir)
        {
            string dll = Path.Combine(gameDir, "Engine.dll");
            if (!File.Exists(dll)) throw new FileNotFoundException("Engine.dll not found", dll);
            if (IntPtr.Size != 4) throw new InvalidOperationException("SptBake must run as a 32-bit process");
            SetDllDirectory(gameDir);
            _module = LoadLibraryEx(dll, IntPtr.Zero, 0);
            if (_module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibrary Engine.dll");
            if ((uint)_module != ExpectedBase)
                throw new InvalidOperationException($"Engine.dll mapped at 0x{(uint)_module:X8}, expected 0x{ExpectedBase:X8}");
            uint stamp = *(uint*)((byte*)_module + *(int*)((byte*)_module + 0x3C) + 8);
            if (stamp != ExpectedTimeStamp)
                throw new InvalidOperationException($"Engine.dll build 0x{stamp:X8} is not CP 1.75 (0x{ExpectedTimeStamp:X8})");
            IntPtr sharedBase = GetModuleHandle("SharedBase.dll");
            IntPtr scaling = sharedBase == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(sharedBase, "?SetToScaling@bCMatrix@@QAEXM@Z");
            if (scaling == IntPtr.Zero) throw new InvalidOperationException("bCMatrix::SetToScaling not found in SharedBase.dll");
            _setToScaling = (delegate* unmanaged[Thiscall]<float*, float, void>)scaling;
        }

        /// <summary>Create a tree exactly like eCWraper_SpeedTreeRT::Create + Read (scale 100: .spt metres → cm).</summary>
        public IntPtr LoadAndCompute(byte[] spt, float scale = 100f)
        {
            var none = new NativeCall.Regs();
            uint tree = _call.Call(FnNew, none, TreeSize);
            if (tree == 0) throw new OutOfMemoryException("engine operator new");
            _call.Call(FnCtor, none, tree);
            Dump("ctor", tree);

            IntPtr buf = Marshal.AllocHGlobal(spt.Length);
            try
            {
                Marshal.Copy(spt, 0, buf, spt.Length);
                uint ok = _call.Call(FnLoadTree, new NativeCall.Regs { Ecx = (uint)buf }, tree, (uint)spt.Length) & 0xFF;
                if (ok == 0) throw new InvalidDataException("CSpeedTreeRT::LoadTree failed");
            }
            finally { Marshal.FreeHGlobal(buf); }
            Dump("load", tree);

            _call.Call(FnSetNumWindMatrices, none);
            _call.Call(FnBranchLighting, new NativeCall.Regs { Ecx = 0 }, tree);   // dynamic lighting → normals
            _call.Call(FnLeafLighting, new NativeCall.Regs { Edx = 0 }, tree);
            _call.Call(FnFrondLighting, new NativeCall.Regs { Ecx = 0 }, tree);
            _call.Call(FnBranchWind, none, tree);                                   // GPU wind → weights + matrix indices
            _call.Call(FnLeafWind, none, tree);
            _call.Call(FnFrondWind, none, tree);
            _call.Call(FnLeafRockGroups, none, tree);

            float* m = stackalloc float[16];
            _setToScaling(m, scale);
            _call.Call(FnCompute, none, tree, (uint)m);

            Dump("compute", tree);
            // Same post-Compute fix-ups as the engine: a flag in the LOD/wind block and LOD distances × tree height (m).
            *((byte*)Ptr(tree + 0x10) + 0x14) = 1;
            return (IntPtr)tree;
        }

        /// <summary>Fill a fresh SGeometry block (caller-owned, <see cref="GeometrySize"/> bytes).</summary>
        public void GetGeometry(IntPtr tree, byte* geometry, int mask, int branchLod, int frondLod, int leafLod)
        {
            _call.Call(FnGeometryCtor, new NativeCall.Regs { Eax = (uint)geometry });
            _call.Call(FnGetGeometry, new NativeCall.Regs { Ecx = (uint)geometry, Edx = (uint)tree },
                (uint)mask, (uint)branchLod, (uint)frondLod, (uint)leafLod);
        }

        public static bool Debug;
        static void Dump(string label, uint t)
        {
            if (!Debug) return;
            Console.Write(label + ": ");
            for (int i = 0; i < TreeSize; i += 4) Console.Write($"{i:X2}:{*(uint*)(t + i):X} ");
            Console.WriteLine();
        }

        public static uint Ptr(uint address) => *(uint*)address;

        public void Dispose() => _call.Dispose();   // Engine.dll stays mapped until process exit (its DllMain is not re-run).

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
