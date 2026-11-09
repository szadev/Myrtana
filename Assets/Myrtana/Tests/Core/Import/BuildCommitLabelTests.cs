using System;
using System.Diagnostics;
using System.IO;
using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>qa-1: "(+ local changes)" only for real content changes, not for line-ending-only rewrites by the editor.</summary>
    public class BuildCommitLabelTests
    {
        [Test]
        public void EmptyNumstatIsClean()
        {
            Assert.AreEqual("5dab17c0", BuildCommitLabel.Format("5dab17c0", ""));
            Assert.AreEqual("5dab17c0", BuildCommitLabel.Format("5dab17c0\n", " \r\n"));
        }

        [Test]
        public void AnyNumstatLineIsDirty()
        {
            Assert.AreEqual("5dab17c0 (+ local changes)", BuildCommitLabel.Format("5dab17c0", "1\t1\tAssets/a.cs\n"));
            Assert.IsTrue(BuildCommitLabel.HasContentChanges("-\t-\tAssets/t.png"));
            Assert.IsTrue(BuildCommitLabel.HasContentChanges("0\t0\tTools/run.sh"));
        }

        static string Git(string dir, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p.ErrorDataReceived += (_, __) => { };
            p.BeginErrorReadLine();
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            return o;
        }

        [Test]
        public void LineEndingOnlyRewriteIsCleanInRealRepo()
        {
            string dir = Path.Combine(Path.GetTempPath(), "commitlabel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                if (Git(dir, "init -q").StartsWith("?")) Assert.Ignore("git unavailable");
                File.WriteAllText(Path.Combine(dir, ".gitattributes"), "* text=auto\n");
                File.WriteAllText(Path.Combine(dir, "a.asset"), "%YAML 1.1\r\nm_Value: 1\r\n");
                Git(dir, "add -A");
                Git(dir, "-c user.name=t -c user.email=t@t -c commit.gpgsign=false commit -q -m init");
                if (Git(dir, "rev-parse --short HEAD").Trim().Length == 0) Assert.Ignore("git commit failed");

                File.WriteAllText(Path.Combine(dir, "a.asset"), "%YAML 1.1\nm_Value: 1\n");   // EOL only
                Assert.IsFalse(BuildCommitLabel.HasContentChanges(Git(dir, BuildCommitLabel.NumstatArgs)), "EOL-only");

                File.WriteAllText(Path.Combine(dir, "a.asset"), "%YAML 1.1\nm_Value: 2\n");   // real change
                Assert.IsTrue(BuildCommitLabel.HasContentChanges(Git(dir, BuildCommitLabel.NumstatArgs)), "content");
            }
            finally
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
        }
    }
}
