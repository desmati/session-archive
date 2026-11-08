using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionArchive;

/// <summary>Copies every source into &lt;folder&gt;/raw/&lt;source&gt;/, additively, and writes manifest.json.</summary>
public sealed class Exporter(string folder, List<Source> sources)
{
    sealed class Tally
    {
        public long Files, Bytes, Copied, CopiedBytes, Unchanged, Scrubbed;
        public readonly ConcurrentQueue<string> Errors = new();
        public readonly ConcurrentQueue<string> Skipped = new();
    }

    public async Task RunAsync()
    {
        var raw = Path.Combine(folder, "raw");
        Directory.CreateDirectory(raw);
        var started = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        var report = new JsonArray();
        foreach (var source in sources)
        {
            var tally = new Tally();
            if (!Directory.Exists(source.Root))
            {
                Console.WriteLine($"{source.Name}: {source.Root} does not exist, skipped");
                report.Add(new JsonObject { ["name"] = source.Name, ["root"] = source.Root, ["missing"] = true });
                continue;
            }
            Console.WriteLine($"{source.Name}: copying {source.Root}");
            var files = Enumerate(source, tally).ToList();
            var dest = Path.Combine(raw, source.Name);
            var done = 0L;
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 6 }, (rel, ct) =>
            {
                CopyOne(source, rel, dest, tally);
                var n = Interlocked.Increment(ref done);
                if (n % 2000 == 0) Console.WriteLine($"  {n:N0} / {files.Count:N0} files");
                return ValueTask.CompletedTask;
            });
            Console.WriteLine($"  {tally.Files:N0} files, {tally.Bytes / 1048576.0:N0} MB; copied {tally.Copied:N0} " +
                              $"({tally.CopiedBytes / 1048576.0:N0} MB), unchanged {tally.Unchanged:N0}, errors {tally.Errors.Count}");
            report.Add(new JsonObject
            {
                ["name"] = source.Name, ["root"] = source.Root, ["description"] = source.Description,
                ["files"] = tally.Files, ["bytes"] = tally.Bytes, ["copied"] = tally.Copied,
                ["copiedBytes"] = tally.CopiedBytes, ["unchanged"] = tally.Unchanged, ["scrubbed"] = tally.Scrubbed,
                ["skipped"] = new JsonArray(tally.Skipped.Take(2000).Select(s => (JsonNode)s).ToArray()),
                ["errors"] = new JsonArray(tally.Errors.Take(2000).Select(s => (JsonNode)s).ToArray()),
                ["errorCount"] = tally.Errors.Count,
            });
        }

        var manifest = new JsonObject
        {
            ["tool"] = "session-archive",
            ["version"] = typeof(Exporter).Assembly.GetName().Version?.ToString(3),
            ["machine"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["exportedAt"] = started.ToString("O"),
            ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
            ["additive"] = "files are added and updated, never deleted: an earlier export's files stay",
            ["alwaysExcluded"] = new JsonArray(Sources.AlwaysExcluded.Select(s => (JsonNode)s).ToArray()),
            ["sources"] = report,
        };
        // Keep each export's report; manifest.json is the latest.
        var history = Path.Combine(folder, "exports");
        Directory.CreateDirectory(history);
        var text = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(history, $"{started:yyyyMMdd-HHmmss}.json"), text);
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), text);
    }

    /// <summary>Walks a source, never following a junction or symbolic link (they can loop, or leave Claude's storage).</summary>
    static IEnumerable<string> Enumerate(Source source, Tally tally)
    {
        var stack = new Stack<string>();
        stack.Push("");
        while (stack.Count > 0)
        {
            var relDir = stack.Pop();
            var dir = relDir.Length == 0 ? source.Root : Path.Combine(source.Root, relDir.Replace('/', '\\'));
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dir).GetFileSystemInfos();
            }
            catch (Exception e)
            {
                tally.Errors.Enqueue($"{relDir}/: {e.Message}");
                continue;
            }
            foreach (var entry in entries)
            {
                var rel = relDir.Length == 0 ? entry.Name : $"{relDir}/{entry.Name}";
                var isDir = entry is DirectoryInfo;
                if (!source.Include(rel, isDir)) continue;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    tally.Skipped.Enqueue($"{rel}: a link ({entry.LinkTarget ?? "?"}), not followed");
                    continue;
                }
                if (isDir)
                {
                    if (!source.SingleLevel) stack.Push(rel);
                }
                else
                {
                    yield return rel;
                }
            }
        }
    }

    static void CopyOne(Source source, string rel, string destRoot, Tally tally)
    {
        var from = Path.Combine(source.Root, rel.Replace('/', '\\'));
        var to = Path.Combine(destRoot, rel.Replace('/', '\\'));
        try
        {
            var info = new FileInfo(from);
            Interlocked.Increment(ref tally.Files);
            Interlocked.Add(ref tally.Bytes, info.Length);
            var scrub = source.Scrub(rel);
            var target = new FileInfo(to);
            if (!scrub && target.Exists && target.Length == info.Length && target.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                Interlocked.Increment(ref tally.Unchanged);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            var tmp = to + ".partial";
            using (var input = new FileStream(from, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (scrub)
                {
                    var bytes = ScrubJson(input, rel, tally);
                    output.Write(bytes);
                }
                else
                {
                    input.CopyTo(output);
                }
            }
            File.Move(tmp, to, overwrite: true);
            File.SetLastWriteTimeUtc(to, info.LastWriteTimeUtc);
            Interlocked.Increment(ref tally.Copied);
            Interlocked.Add(ref tally.CopiedBytes, info.Length);
        }
        catch (FileNotFoundException)
        {
            tally.Skipped.Enqueue($"{rel}: vanished during the copy (a temporary file)");
        }
        catch (DirectoryNotFoundException)
        {
            tally.Skipped.Enqueue($"{rel}: vanished during the copy (a temporary file)");
        }
        catch (Exception e)
        {
            tally.Errors.Enqueue($"{rel}: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Removes, at any depth, every property whose name says it holds a sign-in value.
    /// A file that is not valid JSON is not copied at all, rather than copied with its secrets.</summary>
    static byte[] ScrubJson(Stream input, string rel, Tally tally)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(input);
        }
        catch (JsonException)
        {
            tally.Skipped.Enqueue($"{rel}: not valid JSON, so not copied (it may hold sign-in values)");
            return """{"_sessionArchive":"not copied: not valid JSON, and it may hold sign-in values"}"""u8.ToArray();
        }
        var removed = new List<string>();
        Strip(node, "", removed);
        if (node is JsonObject o)
        {
            o["_sessionArchiveScrubbed"] = new JsonArray(removed.Select(r => (JsonNode)r).ToArray());
        }
        Interlocked.Increment(ref tally.Scrubbed);
        return JsonSerializer.SerializeToUtf8Bytes(node, new JsonSerializerOptions { WriteIndented = true });

        static void Strip(JsonNode? n, string path, List<string> removed)
        {
            switch (n)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        if (Sources.SecretName().IsMatch(key))
                        {
                            obj.Remove(key);
                            removed.Add(path + "/" + key);
                        }
                        else
                        {
                            Strip(obj[key], path + "/" + key, removed);
                        }
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++) Strip(arr[i], $"{path}[{i}]", removed);
                    break;
            }
        }
    }
}
