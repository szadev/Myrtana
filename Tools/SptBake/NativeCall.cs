using System;
using System.Runtime.InteropServices;

namespace G3.Tools.SptBake
{
    /// <summary>
    /// Calls x86 code with an arbitrary register/stack convention. SpeedTreeRT inside Engine.dll was built with
    /// whole-program optimisation, so many functions take arguments in EAX/ECX/EDX next to the stack and some clean
    /// their own stack while others don't. One generic thunk covers all of them: it loads EAX..EDI from a fixed
    /// context block, pushes the stack arguments, calls the target and afterwards restores ESP from EBP, so both
    /// caller- and callee-cleanup work. Not thread-safe (one context block); the baker is single-threaded.
    /// </summary>
    public sealed unsafe class NativeCall : IDisposable
    {
        // Context block layout (offsets used by the thunk code below).
        const int OffTarget = 0x00, OffEax = 0x04, OffEcx = 0x08, OffEdx = 0x0C, OffEbx = 0x10, OffEsi = 0x14, OffEdi = 0x18,
                  OffArgCount = 0x1C, OffArgs = 0x20, MaxArgs = 16, OffRetEax = 0x60, OffRetEdx = 0x64, OffWantFloat = 0x68,
                  OffRetFloat = 0x6C, CtxSize = 0x80;

        readonly byte* _ctx;
        readonly byte* _code;
        readonly delegate* unmanaged[Stdcall]<void> _thunk;

        public NativeCall()
        {
            _ctx = (byte*)VirtualAlloc(IntPtr.Zero, (UIntPtr)CtxSize, MemCommit | MemReserve, PageReadWrite);
            _code = (byte*)VirtualAlloc(IntPtr.Zero, (UIntPtr)256, MemCommit | MemReserve, PageExecuteReadWrite);
            if (_ctx == null || _code == null) throw new InvalidOperationException("VirtualAlloc failed");
            uint c = (uint)_ctx;
            var asm = new Asm(_code);
            asm.B(0x55);                              // push ebp
            asm.B(0x89, 0xE5);                        // mov ebp, esp
            asm.B(0x53, 0x56, 0x57);                  // push ebx; push esi; push edi
            asm.B(0xBE); asm.D(c);                    // mov esi, ctx
            asm.B(0x8B, 0x4E, OffArgCount);           // mov ecx, [esi+argc]
            asm.B(0x85, 0xC9);                        // loop: test ecx, ecx
            asm.B(0x74, 0x07);                        //       jz done
            asm.B(0x49);                              //       dec ecx
            asm.B(0xFF, 0x74, 0x8E, OffArgs);         //       push dword [esi+ecx*4+args]
            asm.B(0xEB, 0xF5);                        //       jmp loop
            asm.B(0x8B, 0x46, OffEax);                // done: mov eax, [esi+eax]
            asm.B(0x8B, 0x4E, OffEcx);                // mov ecx, ...
            asm.B(0x8B, 0x56, OffEdx);                // mov edx, ...
            asm.B(0x8B, 0x5E, OffEbx);                // mov ebx, ...
            asm.B(0x8B, 0x7E, OffEdi);                // mov edi, ...
            asm.B(0x8B, 0x76, OffEsi);                // mov esi, ... (last: base register)
            asm.B(0xFF, 0x15); asm.D(c + OffTarget);  // call [ctx+target]
            asm.B(0xBE); asm.D(c);                    // mov esi, ctx
            asm.B(0x89, 0x46, OffRetEax);             // mov [esi+reteax], eax
            asm.B(0x89, 0x56, OffRetEdx);             // mov [esi+retedx], edx
            asm.B(0x83, 0x7E, OffWantFloat, 0x00);    // cmp dword [esi+wantfloat], 0
            asm.B(0x74, 0x03);                        // je +3
            asm.B(0xD9, 0x5E, OffRetFloat);           // fstp dword [esi+retfloat]
            asm.B(0x8D, 0x65, 0xF4);                  // lea esp, [ebp-12]
            asm.B(0x5F, 0x5E, 0x5B, 0x5D);            // pop edi; pop esi; pop ebx; pop ebp
            asm.B(0xC3);                              // ret
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)_code, (UIntPtr)256);
            _thunk = (delegate* unmanaged[Stdcall]<void>)_code;
        }

        /// <summary>Register inputs for one call; unset registers are zero.</summary>
        public struct Regs
        {
            public uint Eax, Ecx, Edx, Ebx, Esi, Edi;
        }

        /// <summary>Call <paramref name="target"/> with the given registers and stack arguments (first argument pushed last). Returns EAX.</summary>
        public uint Call(uint target, Regs regs, params uint[] stackArgs)
        {
            Prepare(target, regs, stackArgs, false);
            _thunk();
            return *(uint*)(_ctx + OffRetEax);
        }

        /// <summary>Like <see cref="Call"/> for functions that return a float on the x87 stack.</summary>
        public float CallFloat(uint target, Regs regs, params uint[] stackArgs)
        {
            Prepare(target, regs, stackArgs, true);
            _thunk();
            return *(float*)(_ctx + OffRetFloat);
        }

        void Prepare(uint target, Regs r, uint[] args, bool wantFloat)
        {
            if (args.Length > MaxArgs) throw new ArgumentException("too many stack arguments");
            *(uint*)(_ctx + OffTarget) = target;
            *(uint*)(_ctx + OffEax) = r.Eax; *(uint*)(_ctx + OffEcx) = r.Ecx; *(uint*)(_ctx + OffEdx) = r.Edx;
            *(uint*)(_ctx + OffEbx) = r.Ebx; *(uint*)(_ctx + OffEsi) = r.Esi; *(uint*)(_ctx + OffEdi) = r.Edi;
            *(uint*)(_ctx + OffArgCount) = (uint)args.Length;
            for (int i = 0; i < args.Length; i++) *(uint*)(_ctx + OffArgs + 4 * i) = args[i];
            *(uint*)(_ctx + OffWantFloat) = wantFloat ? 1u : 0u;
        }

        public static uint F(float v) => *(uint*)&v;

        public void Dispose()
        {
            VirtualFree((IntPtr)_code, UIntPtr.Zero, MemRelease);
            VirtualFree((IntPtr)_ctx, UIntPtr.Zero, MemRelease);
        }

        struct Asm
        {
            byte* _p;
            public Asm(byte* p) { _p = p; }
            public void B(params int[] bytes) { foreach (var b in bytes) *_p++ = (byte)b; }
            public void D(uint v) { *(uint*)_p = v; _p += 4; }
        }

        const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 0x04, PageExecuteReadWrite = 0x40;
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32", SetLastError = true)] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32")] static extern bool FlushInstructionCache(IntPtr process, IntPtr addr, UIntPtr size);
        [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
    }
}
