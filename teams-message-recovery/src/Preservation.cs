// Teams Message History - find standard Teams storage folders and make a read-only preservation copy.
// The original folders are only ever opened for reading (FileShare.ReadWrite | Delete). Nothing is
// written into them, and no Teams process is started or stopped.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace TeamsMessageHistory
{
    public sealed class SourceCandidate
    {
        public string Kind;        // teams-new, teams-classic, edge, chrome, custom
        public string Label;
        public string LevelDbPath;
        public string BlobPath;    // sibling *.indexeddb.blob folder, or null
        public long Bytes;
        public DateTime? LastWriteUtc;
        public int FileCount;

        public OrderedMap ToMap()
        {
            OrderedMap m = new OrderedMap();
            m.Set("kind", Kind);
            m.Set("label", Label);
            m.Set("levelDbPath", LevelDbPath);
            m.Set("blobPath", BlobPath);
            m.Set("bytes", Bytes);
            m.Set("lastWriteUtc", LastWriteUtc.HasValue ? LastWriteUtc.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null);
            m.Set("fileCount", FileCount);
            return m;
        }
    }

    public static class SourceLocator
    {
        /// <summary>Standard Teams storage folders on this Windows user profile. Only known roots are examined; no disk-wide search.</summary>
        public static List<SourceCandidate> FindStandardLocations(List<string> notes)
        {
            List<SourceCandidate> result = new List<SourceCandidate>();
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(localAppData))
            {
                notes.Add("環境変数 LOCALAPPDATA が無いため、標準の保存先は探していません。");
                return result;
            }
            // New Teams (MSIX package, WebView2 profile per account type)
            AddMatches(result, "teams-new", "新しい Teams",
                Path.Combine(localAppData, "Packages"),
                new string[] { "MSTeams_*", "LocalCache", "Microsoft", "MSTeams", "EBWebView", "*", "IndexedDB", "https_teams.*.indexeddb.leveldb" }, notes);
            // Classic Teams (Electron)
            if (!string.IsNullOrEmpty(appData))
            {
                AddMatches(result, "teams-classic", "従来の Teams",
                    Path.Combine(appData, "Microsoft"),
                    new string[] { "Teams", "IndexedDB", "https_teams.*.indexeddb.leveldb" }, notes);
            }
            // Teams used in a browser
            AddMatches(result, "edge", "Microsoft Edge のプロファイル",
                Path.Combine(Path.Combine(localAppData, "Microsoft"), "Edge"),
                new string[] { "User Data", "*", "IndexedDB", "https_teams.*.indexeddb.leveldb" }, notes);
            AddMatches(result, "chrome", "Google Chrome のプロファイル",
                Path.Combine(Path.Combine(localAppData, "Google"), "Chrome"),
                new string[] { "User Data", "*", "IndexedDB", "https_teams.*.indexeddb.leveldb" }, notes);
            return result;
        }

        private static void AddMatches(List<SourceCandidate> result, string kind, string label, string root, string[] segments, List<string> notes)
        {
            if (!Directory.Exists(root)) return;
            List<string> found = new List<string>();
            Glob(root, segments, 0, found);
            foreach (string path in found)
            {
                SourceCandidate c = Describe(path, kind);
                if (c == null) continue;
                string parentName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path.TrimEnd('\\', '/'))));
                c.Label = label + " [" + parentName + "] " + Path.GetFileName(path.TrimEnd('\\', '/'));
                result.Add(c);
            }
        }

        private static void Glob(string current, string[] segments, int index, List<string> found)
        {
            if (index >= segments.Length)
            {
                found.Add(current);
                return;
            }
            string pattern = segments[index];
            string[] children;
            try
            {
                children = Directory.GetDirectories(current, pattern);
            }
            catch (Exception)
            {
                return;
            }
            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            foreach (string child in children) Glob(child, segments, index + 1, found);
        }

        /// <summary>Describe one LevelDB folder (size, newest write, sibling blob folder). Returns null when it is not a LevelDB folder.</summary>
        public static SourceCandidate Describe(string levelDbPath, string kind)
        {
            if (!IsLevelDbFolder(levelDbPath)) return null;
            SourceCandidate c = new SourceCandidate();
            c.Kind = kind;
            c.LevelDbPath = levelDbPath;
            string trimmed = levelDbPath.TrimEnd('\\', '/');
            string name = Path.GetFileName(trimmed);
            if (name.EndsWith(".leveldb", StringComparison.OrdinalIgnoreCase))
            {
                string blob = Path.Combine(Path.GetDirectoryName(trimmed), name.Substring(0, name.Length - ".leveldb".Length) + ".blob");
                if (Directory.Exists(blob)) c.BlobPath = blob;
            }
            foreach (string file in Directory.GetFiles(levelDbPath))
            {
                FileInfo fi = new FileInfo(file);
                c.Bytes += fi.Length;
                c.FileCount++;
                DateTime w = fi.LastWriteTimeUtc;
                if (!c.LastWriteUtc.HasValue || w > c.LastWriteUtc.Value) c.LastWriteUtc = w;
            }
            c.Label = name;
            return c;
        }

        public static bool IsLevelDbFolder(string path)
        {
            if (!Directory.Exists(path)) return false;
            if (File.Exists(Path.Combine(path, "CURRENT"))) return true;
            if (Directory.GetFiles(path, "MANIFEST-*").Length > 0) return true;
            if (Directory.GetFiles(path, "*.ldb").Length > 0 || Directory.GetFiles(path, "*.log").Length > 0) return true;
            return false;
        }

        /// <summary>A folder the user collected earlier: either a LevelDB folder itself or a folder containing *.indexeddb.leveldb folders.</summary>
        public static List<SourceCandidate> FromUserPath(string path, List<string> notes)
        {
            List<SourceCandidate> result = new List<SourceCandidate>();
            if (string.IsNullOrEmpty(path)) return result;
            path = path.Trim().Trim('"');
            if (!Directory.Exists(path))
            {
                notes.Add("フォルダーが見つかりません: " + path);
                return result;
            }
            if (IsLevelDbFolder(path))
            {
                SourceCandidate c = Describe(path, "custom");
                if (c != null) result.Add(c);
                return result;
            }
            List<string> found = new List<string>();
            FindLevelDbFolders(path, 0, found);
            foreach (string f in found)
            {
                SourceCandidate c = Describe(f, "custom");
                if (c != null) result.Add(c);
            }
            if (result.Count == 0) notes.Add("指定フォルダーの中に LevelDB（*.indexeddb.leveldb など）が見つかりません: " + path);
            return result;
        }

        private static void FindLevelDbFolders(string current, int depth, List<string> found)
        {
            if (depth > 4) return;
            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                return;
            }
            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (name.EndsWith(".blob", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsLevelDbFolder(child)) found.Add(child);
                else FindLevelDbFolders(child, depth + 1, found);
            }
        }
    }

    public sealed class PreservedFile
    {
        public string RelativePath;
        public long SourceBytes;
        public DateTime? SourceLastWriteUtc;
        public long CopiedBytes;
        public string Sha256;
        public string Error;

        public OrderedMap ToMap()
        {
            OrderedMap m = new OrderedMap();
            m.Set("relativePath", RelativePath);
            m.Set("sourceBytes", SourceBytes);
            m.Set("sourceLastWriteUtc", SourceLastWriteUtc.HasValue ? SourceLastWriteUtc.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null);
            m.Set("copiedBytes", CopiedBytes);
            m.Set("sha256", Sha256);
            m.Set("error", Error);
            return m;
        }
    }

    public sealed class PreservedCopy
    {
        public SourceCandidate Source;
        public string DestinationLevelDb;
        public string DestinationBlob;
        public string StartedUtc;
        public string FinishedUtc;
        public string ManifestPath;
        public readonly List<PreservedFile> Files = new List<PreservedFile>();
        public bool Complete;
        public bool? UnchangedAfterAnalysis;

        public OrderedMap ToMap()
        {
            OrderedMap m = new OrderedMap();
            m.Set("source", Source.ToMap());
            m.Set("destinationLevelDb", DestinationLevelDb);
            m.Set("destinationBlob", DestinationBlob);
            m.Set("startedUtc", StartedUtc);
            m.Set("finishedUtc", FinishedUtc);
            m.Set("mode", "Live read-only file copy (FileShare.ReadWrite|Delete); no point-in-time consistency guarantee; originals untouched");
            m.Set("complete", Complete);
            m.Set("unchangedAfterAnalysis", UnchangedAfterAnalysis.HasValue ? (object)UnchangedAfterAnalysis.Value : null);
            List<object> files = new List<object>();
            foreach (PreservedFile f in Files) files.Add(f.ToMap());
            m.Set("files", files);
            return m;
        }
    }

    public static class Preservation
    {
        public static PreservedCopy Copy(SourceCandidate source, string destinationRoot, int index, Action<string> progress)
        {
            PreservedCopy copy = new PreservedCopy();
            copy.Source = source;
            copy.StartedUtc = TimeText.NowIso();
            string baseName = index.ToString("00", CultureInfo.InvariantCulture) + "_" + Path.GetFileName(source.LevelDbPath.TrimEnd('\\', '/'));
            copy.DestinationLevelDb = Path.Combine(destinationRoot, baseName);
            Directory.CreateDirectory(copy.DestinationLevelDb);
            bool allOk = true;
            foreach (string file in SortedFiles(source.LevelDbPath))
            {
                PreservedFile pf = CopyOne(file, Path.Combine(copy.DestinationLevelDb, Path.GetFileName(file)), Path.GetFileName(file));
                copy.Files.Add(pf);
                if (pf.Error != null) allOk = false;
                if (progress != null) progress("  複製: " + pf.RelativePath + (pf.Error != null ? " -> 失敗: " + pf.Error : " (" + pf.CopiedBytes.ToString(CultureInfo.InvariantCulture) + " bytes)"));
            }
            if (source.BlobPath != null && Directory.Exists(source.BlobPath))
            {
                string blobName = index.ToString("00", CultureInfo.InvariantCulture) + "_" + Path.GetFileName(source.BlobPath.TrimEnd('\\', '/'));
                copy.DestinationBlob = Path.Combine(destinationRoot, blobName);
                CopyTree(source.BlobPath, copy.DestinationBlob, "", copy, ref allOk, progress);
            }
            copy.Complete = allOk;
            copy.FinishedUtc = TimeText.NowIso();
            copy.ManifestPath = Path.Combine(destinationRoot, "manifest-" + index.ToString("00", CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(copy.ManifestPath, JsonWriter.Serialize(copy.ToMap()), new System.Text.UTF8Encoding(false));
            return copy;
        }

        private static List<string> SortedFiles(string folder)
        {
            List<string> files = new List<string>(Directory.GetFiles(folder));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static void CopyTree(string sourceDir, string destDir, string relative, PreservedCopy copy, ref bool allOk, Action<string> progress)
        {
            Directory.CreateDirectory(destDir);
            foreach (string file in SortedFiles(sourceDir))
            {
                string rel = (relative.Length == 0 ? "" : relative + "/") + Path.GetFileName(file);
                PreservedFile pf = CopyOne(file, Path.Combine(destDir, Path.GetFileName(file)), "blob/" + rel);
                copy.Files.Add(pf);
                if (pf.Error != null) allOk = false;
            }
            string[] dirs = Directory.GetDirectories(sourceDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                string rel = (relative.Length == 0 ? "" : relative + "/") + Path.GetFileName(dir);
                CopyTree(dir, Path.Combine(destDir, Path.GetFileName(dir)), rel, copy, ref allOk, progress);
            }
        }

        private static PreservedFile CopyOne(string sourcePath, string destPath, string relativePath)
        {
            PreservedFile pf = new PreservedFile();
            pf.RelativePath = relativePath;
            try
            {
                FileInfo fi = new FileInfo(sourcePath);
                pf.SourceBytes = fi.Length;
                pf.SourceLastWriteUtc = fi.LastWriteTimeUtc;
                using (SHA256 sha = SHA256.Create())
                using (FileStream input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (FileStream output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    long total = 0;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        total += read;
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    pf.CopiedBytes = total;
                    pf.Sha256 = Hex.ToHex(sha.Hash);
                }
            }
            catch (Exception ex)
            {
                pf.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return pf;
        }

        /// <summary>Re-hash the preserved files; true when every successfully copied file still has the same SHA-256.</summary>
        public static bool VerifyUnchanged(PreservedCopy copy, List<string> changed)
        {
            bool ok = true;
            foreach (PreservedFile pf in copy.Files)
            {
                if (pf.Error != null || pf.Sha256 == null) continue;
                string path = pf.RelativePath.StartsWith("blob/", StringComparison.Ordinal)
                    ? Path.Combine(copy.DestinationBlob, pf.RelativePath.Substring(5).Replace('/', Path.DirectorySeparatorChar))
                    : Path.Combine(copy.DestinationLevelDb, pf.RelativePath);
                try
                {
                    string now = Sha256Util.HexOfFile(path);
                    if (!string.Equals(now, pf.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        ok = false;
                        changed.Add(pf.RelativePath);
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    changed.Add(pf.RelativePath + " (" + ex.Message + ")");
                }
            }
            copy.UnchangedAfterAnalysis = ok;
            return ok;
        }
    }
}
