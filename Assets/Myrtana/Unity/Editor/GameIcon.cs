using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// fix-165: Gothic 3's own exe icon for the build (Docs/build.md "Icon"). <see cref="Extract"/> reads the icon group of
    /// the install's game exe (<see cref="SourceNames"/>), all images as they are, into a plain <c>.ico</c> (game-derived: only in the
    /// git-ignored Library folder, never in git); <see cref="Apply"/> puts the images of an <c>.ico</c> into an exe as its
    /// icon, replacing the icons it has under the same group name and language (the Unity player's group 103, which
    /// UnityPlayer also loads for the window and taskbar icon), so nothing in ProjectSettings points at an ignored file.
    /// Plain Win32 resource API, no Unity types (testable outside the editor).
    /// </summary>
    public static class GameIcon
    {
        /// <summary>
        /// Where the icon comes from, first found wins. The Steam install's <c>Gothic3.exe</c> is a 147 KB start stub with
        /// THQ Nordic's blue "n" icon; the game itself, <c>Gothic3Final.exe</c>, has Gothic 3's "G" (16..64 px, 4 and 8 bit).
        /// An install without it (retail: Gothic3.exe is the game) falls back to <c>Gothic3.exe</c>.
        /// </summary>
        public static readonly string[] SourceNames = { "Gothic3Final.exe", "Gothic3.exe" };

        const uint LoadAsData = 0x00000002 | 0x00000020; // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE
        static readonly IntPtr RtIcon = (IntPtr)3, RtGroupIcon = (IntPtr)14;
        delegate bool EnumNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
        delegate bool EnumLangProc(IntPtr module, IntPtr type, IntPtr name, ushort lang, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumNameProc proc, IntPtr param);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool EnumResourceLanguagesW(IntPtr module, IntPtr type, IntPtr name, EnumLangProc proc, IntPtr param);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindResourceExW(IntPtr module, IntPtr type, IntPtr name, ushort lang);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr res);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
        [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr res);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr BeginUpdateResourceW(string path, bool deleteExisting);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UpdateResourceW(IntPtr update, IntPtr type, string name, ushort lang, byte[] data, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool EndUpdateResourceW(IntPtr update, bool discard);

        /// <summary>One icon image: its 12-byte directory head (width, height, colours, reserved, planes, bit count) and data.</summary>
        public readonly struct Image
        {
            public readonly byte[] Head; // GRPICONDIRENTRY/ICONDIRENTRY bytes 0..7 (dwBytesInRes follows)
            public readonly byte[] Data;
            public Image(byte[] head, byte[] data) { Head = head; Data = data; }
            public int Width => Head[0] == 0 ? 256 : Head[0];
            public int Height => Head[1] == 0 ? 256 : Head[1];
            public int BitCount => BitConverter.ToUInt16(Head, 6);
            public override string ToString() => $"{Width}x{Height}/{BitCount}";
        }

        /// <summary>A resource name: an integer id or a string (kept as text, valid after the module is freed).</summary>
        readonly struct ResName
        {
            public readonly int Id; public readonly string Text;
            ResName(int id, string text) { Id = id; Text = text; }
            public static ResName Of(IntPtr p) => (long)p >> 16 == 0 ? new ResName((int)p, null) : new ResName(0, Marshal.PtrToStringUni(p));
            public override string ToString() => Text ?? Id.ToString();
        }

        // ---- reading ----

        static byte[] Resource(IntPtr module, IntPtr type, IntPtr name, ushort lang)
        {
            IntPtr res = FindResourceExW(module, type, name, lang);
            if (res == IntPtr.Zero) return null;
            IntPtr ptr = LockResource(LoadResource(module, res));
            if (ptr == IntPtr.Zero) return null;
            var data = new byte[SizeofResource(module, res)];
            Marshal.Copy(ptr, data, 0, data.Length);
            return data;
        }

        static List<(ResName name, ushort lang)> Entries(IntPtr module, IntPtr type)
        {
            var names = new List<ResName>();
            var ptrs = new List<IntPtr>();
            EnumResourceNamesW(module, type, (m, t, n, p) => { names.Add(ResName.Of(n)); ptrs.Add(n); return true; }, IntPtr.Zero);
            var all = new List<(ResName, ushort)>();
            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                IntPtr np = name.Text == null ? (IntPtr)name.Id : ptrs[i];
                EnumResourceLanguagesW(module, type, np, (m, t, n, l, p) => { all.Add((name, l)); return true; }, IntPtr.Zero);
            }
            return all;
        }

        /// <summary>The first icon group of the exe/dll <paramref name="path"/> (all its images), or null with the reason.</summary>
        public static List<Image> ReadExe(string path, out string report)
        {
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadAsData);
            if (module == IntPtr.Zero) { report = Path.GetFileName(path) + " not loadable (" + Marshal.GetLastWin32Error() + ")"; return null; }
            try
            {
                var groups = Entries(module, RtGroupIcon);
                if (groups.Count == 0 || groups[0].name.Text != null) { report = "no (numbered) icon group in " + Path.GetFileName(path); return null; }
                var (name, lang) = groups[0];
                byte[] dir = Resource(module, RtGroupIcon, (IntPtr)name.Id, lang);
                if (dir == null || dir.Length < 6) { report = "unreadable icon group in " + Path.GetFileName(path); return null; }
                int count = BitConverter.ToUInt16(dir, 4);
                var images = new List<Image>();
                for (int i = 0; i < count && 6 + i * 14 + 14 <= dir.Length; i++)
                {
                    int e = 6 + i * 14;
                    ushort id = BitConverter.ToUInt16(dir, e + 12);
                    byte[] img = Resource(module, RtIcon, (IntPtr)id, lang) ?? Resource(module, RtIcon, (IntPtr)id, 0);
                    if (img == null) continue;
                    var head = new byte[8];
                    Array.Copy(dir, e, head, 0, 8);
                    images.Add(new Image(head, img));
                }
                report = images.Count == 0 ? "no icon images in " + Path.GetFileName(path) : null;
                return images.Count == 0 ? null : images;
            }
            finally { FreeLibrary(module); }
        }

        /// <summary>The images of an <c>.ico</c> file.</summary>
        public static List<Image> ReadIco(byte[] ico)
        {
            if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1) throw new InvalidDataException("not an .ico");
            int count = BitConverter.ToUInt16(ico, 4);
            var images = new List<Image>();
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                if (e + 16 > ico.Length) throw new InvalidDataException("truncated .ico directory");
                int size = BitConverter.ToInt32(ico, e + 8), offset = BitConverter.ToInt32(ico, e + 12);
                if (size <= 0 || offset < 0 || offset + size > ico.Length) throw new InvalidDataException("bad .ico image " + i);
                var head = new byte[8];
                Array.Copy(ico, e, head, 0, 8);
                var data = new byte[size];
                Array.Copy(ico, offset, data, 0, size);
                images.Add(new Image(head, data));
            }
            if (images.Count == 0) throw new InvalidDataException("empty .ico");
            return images;
        }

        /// <summary>An <c>.ico</c> file of <paramref name="images"/> (ICONDIR + 16-byte entries + image data).</summary>
        public static byte[] WriteIco(IReadOnlyList<Image> images)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var img in images) { w.Write(img.Head); w.Write(img.Data.Length); w.Write(offset); offset += img.Data.Length; }
            foreach (var img in images) w.Write(img.Data);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>
        /// The icon of <paramref name="sourceExe"/> as <paramref name="icoPath"/> (rewritten when the exe is newer); null when
        /// there is none (<paramref name="report"/> says why).
        /// </summary>
        public static string Extract(string sourceExe, string icoPath, out string report)
        {
            if (!File.Exists(sourceExe)) { report = "no " + sourceExe; return null; }
            try
            {
                if (File.Exists(icoPath) && File.GetLastWriteTimeUtc(icoPath) >= File.GetLastWriteTimeUtc(sourceExe))
                {
                    var cached = ReadIco(File.ReadAllBytes(icoPath));
                    report = Describe(cached) + " from " + sourceExe;
                    return icoPath;
                }
                var images = ReadExe(sourceExe, out report);
                if (images == null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(icoPath));
                File.WriteAllBytes(icoPath, WriteIco(images));
                report = Describe(images) + " from " + sourceExe;
                return icoPath;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
            {
                report = sourceExe + " icon not usable (" + e.Message + ")";
                return null;
            }
        }

        public static string Describe(IReadOnlyList<Image> images) => images.Count + " icon images (" + string.Join(", ", images) + ")";

        // ---- writing ----

        /// <summary>
        /// Makes the <c>.ico</c> <paramref name="icoPath"/> the icon of <paramref name="exe"/>: every icon group and icon
        /// image it has is removed, the new images become RT_ICON 1..n and the group keeps the exe's first group name and
        /// language (Unity player: 103, en-US), or group 1, neutral, for an exe without an icon (the launcher).
        /// </summary>
        public static bool Apply(string exe, string icoPath, out string report)
        {
            List<Image> images;
            try { images = ReadIco(File.ReadAllBytes(icoPath)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
            { report = icoPath + " not usable (" + e.Message + ")"; return false; }

            List<(ResName name, ushort lang)> oldGroups, oldIcons;
            IntPtr module = LoadLibraryExW(exe, IntPtr.Zero, LoadAsData);
            if (module == IntPtr.Zero) { report = Path.GetFileName(exe) + " not loadable (" + Marshal.GetLastWin32Error() + ")"; return false; }
            try { oldGroups = Entries(module, RtGroupIcon); oldIcons = Entries(module, RtIcon); }
            finally { FreeLibrary(module); }

            ResName group = oldGroups.Count > 0 ? oldGroups[0].name : default;
            int groupId = oldGroups.Count > 0 && group.Text == null ? group.Id : 1;
            string groupText = oldGroups.Count > 0 ? group.Text : null;
            ushort lang = oldGroups.Count > 0 ? oldGroups[0].lang : (ushort)0;

            IntPtr update = BeginUpdateResourceW(exe, false);
            if (update == IntPtr.Zero) { report = Path.GetFileName(exe) + " not writable (" + Marshal.GetLastWin32Error() + ")"; return false; }
            bool ok = true;
            foreach (var (name, l) in oldGroups) ok &= Put(update, RtGroupIcon, name, l, null);
            foreach (var (name, l) in oldIcons) ok &= Put(update, RtIcon, name, l, null);
            var dir = new byte[6 + 14 * images.Count];
            BitConverter.GetBytes((ushort)1).CopyTo(dir, 2);
            BitConverter.GetBytes((ushort)images.Count).CopyTo(dir, 4);
            for (int i = 0; i < images.Count; i++)
            {
                int e = 6 + 14 * i;
                images[i].Head.CopyTo(dir, e);
                BitConverter.GetBytes(images[i].Data.Length).CopyTo(dir, e + 8);
                BitConverter.GetBytes((ushort)(i + 1)).CopyTo(dir, e + 12);
                ok &= UpdateResourceW(update, RtIcon, (IntPtr)(i + 1), lang, images[i].Data, (uint)images[i].Data.Length);
            }
            ok &= groupText != null
                ? UpdateResourceW(update, RtGroupIcon, groupText, lang, dir, (uint)dir.Length)
                : UpdateResourceW(update, RtGroupIcon, (IntPtr)groupId, lang, dir, (uint)dir.Length);
            int error = Marshal.GetLastWin32Error();
            if (!EndUpdateResourceW(update, !ok) || !ok)
            {
                report = Path.GetFileName(exe) + " icon update failed (" + (ok ? Marshal.GetLastWin32Error() : error) + ")";
                return false;
            }
            report = $"{Describe(images)} as group {groupText ?? groupId.ToString()} (replaced {oldGroups.Count} groups, {oldIcons.Count} images)";
            return true;
        }

        static bool Put(IntPtr update, IntPtr type, ResName name, ushort lang, byte[] data) => name.Text != null
            ? UpdateResourceW(update, type, name.Text, lang, data, 0)
            : UpdateResourceW(update, type, (IntPtr)name.Id, lang, data, 0);
    }
}
