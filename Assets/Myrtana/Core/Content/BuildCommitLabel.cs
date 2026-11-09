using System;

namespace Myrtana.Core
{
    /// <summary>
    /// qa-1: the commit line of BUILD_INFO/README ("5dab17c0" or "5dab17c0 (+ local changes)"). The editor rewrites
    /// some assets (Assets/Settings/PC_RPAsset.asset) with other line endings only; <c>git status</c> lists such a file as
    /// modified, but <c>git diff HEAD --numstat</c> compares the normalised (autocrlf/eol) content and prints nothing for
    /// it. So only a non-empty numstat marks the build as built from local changes.
    /// </summary>
    public static class BuildCommitLabel
    {
        public const string LocalChanges = " (+ local changes)";

        /// <summary>Git arguments whose output <see cref="HasContentChanges"/> reads (tracked files, staged or not).</summary>
        public const string NumstatArgs = "-c core.safecrlf=false diff HEAD --numstat";

        /// <summary>True when <c>git diff HEAD --numstat</c> lists any file (text lines changed, binary "-\t-", mode change).</summary>
        public static bool HasContentChanges(string numstat)
        {
            if (string.IsNullOrEmpty(numstat)) return false;
            foreach (var line in numstat.Split('\n'))
                if (line.Trim().Length > 0) return true;
            return false;
        }

        public static string Format(string shortHash, string numstat) =>
            (shortHash ?? "").Trim() + (HasContentChanges(numstat) ? LocalChanges : "");
    }
}
