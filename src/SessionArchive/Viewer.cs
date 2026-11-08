using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;

namespace SessionArchive;

/// <summary>The local viewer: a read-only JSON API over index.db and raw/, plus the embedded web page.
/// Listens on 127.0.0.1 only. It never writes to the export and never serves a file outside raw/, except a
/// file a session touched (by its recorded path), which is the "link to the local file".</summary>
public static class Viewer
{
    const int MaxText = 30_000, MaxOtherText = 5_000;

    public static async Task RunAsync(string folder, int port, bool openBrowser)
    {
        var raw = Path.GetFullPath(Path.Combine(folder, "raw"));
        var dbPath = Path.Combine(folder, "index.db");
        if (!File.Exists(dbPath)) throw new UserError($"{dbPath} not found: run `session-archive export \"{folder}\"` first");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var connString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        SqliteConnection Open()
        {
            var c = new SqliteConnection(connString);
            c.Open();
            return c;
        }
        var types = new FileExtensionContentTypeProvider();

        // Only this machine's browser, by this address: a page elsewhere cannot reach the export through a
        // DNS name pointed at 127.0.0.1 (DNS rebinding), and nothing served from the export runs as a page.
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{port}", $"localhost:{port}" };
        app.Use(async (ctx, next) =>
        {
            if (!allowedHosts.Contains(ctx.Request.Host.Value ?? ""))
            {
                ctx.Response.StatusCode = 421;
                return;
            }
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (ctx.Request.Path.StartsWithSegments("/api/raw") || ctx.Request.Path.StartsWithSegments("/api/local")
                || ctx.Request.Path.StartsWithSegments("/api/record"))
                ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'";
            else
                ctx.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: https:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
            await next();
        });

        // ------------------------------------------------------------ the page
        foreach (var (route, name, type) in new[]
                 {
                     ("/", "index.html", "text/html; charset=utf-8"),
                     ("/app.js", "app.js", "text/javascript; charset=utf-8"),
                     ("/app.css", "app.css", "text/css; charset=utf-8"),
                 })
        {
            app.MapGet(route, () => Results.Stream(Resource(name), type));
        }

        // ------------------------------------------------------------ overview
        app.MapGet("/api/info", () =>
        {
            using var db = Open();
            var info = Rows(db, "SELECT key, value FROM info").ToDictionary(r => (string)r["key"]!, r => r["value"]);
            var counts = Rows(db, """
                SELECT (SELECT count(*) FROM sessions) sessions, (SELECT count(*) FROM transcripts) transcripts,
                       (SELECT count(*) FROM records) records, (SELECT count(*) FROM memory) memory,
                       (SELECT count(*) FROM session_dirs) dirs
                """).First();
            return Results.Json(new { folder, info, counts });
        });

        app.MapGet("/api/sessions", () =>
        {
            using var db = Open();
            return Results.Json(Rows(db, "SELECT * FROM sessions ORDER BY last_activity DESC"));
        });

        app.MapGet("/api/session/{key}", (string key) =>
        {
            using var db = Open();
            var s = Rows(db, "SELECT * FROM sessions WHERE key=$k", ("$k", key)).FirstOrDefault();
            if (s == null) return Results.NotFound();
            var transcripts = Rows(db, """
                SELECT id, path, sid, kind, parent_sid, agent_id, meta, project, lines, bytes, first_ts, last_ts, title,
                       agent_name, first_prompt, cwd, git_branch, user_msgs, assistant_msgs, tool_calls,
                       (SELECT count(*) FROM records r WHERE r.tid=t.id AND r.visible=1) visible
                FROM transcripts t WHERE session_key=$k ORDER BY kind='main' DESC, first_ts
                """, ("$k", key));
            var dirs = Rows(db, "SELECT label, path, files, bytes FROM session_dirs WHERE session_key=$k ORDER BY label, path", ("$k", key));
            var touched = Rows(db, """
                SELECT path, group_concat(op || ' x' || count, ', ') ops, sum(count) count, min(first_ts) first_ts,
                       max(last_ts) last_ts, min(tid) tid, min(first_line) first_line
                FROM touched WHERE session_key=$k GROUP BY path ORDER BY max(last_ts) DESC
                """, ("$k", key));
            foreach (var t in touched) t["exists"] = SafeExists((string?)t["path"]);
            var backups = Rows(db, "SELECT path, backup, version, time, present FROM backups WHERE session_key=$k ORDER BY path, version", ("$k", key));
            var memory = Rows(db, "SELECT id, path, name, bytes, mtime FROM memory WHERE project=$p ORDER BY name", ("$p", s["project"]));
            string? meta = null;
            if (s["meta_path"] is string mp && Safe(raw, mp) is { } metaFile && File.Exists(metaFile)) meta = File.ReadAllText(metaFile);
            return Results.Json(new { session = s, transcripts, dirs, touched, backups, memory, meta });
        });

        // ------------------------------------------------------------ the conversation
        app.MapGet("/api/transcript/{tid:long}/records", (long tid, long? from, long? before, int? count, int? all) =>
        {
            using var db = Open();
            var t = Rows(db, "SELECT path FROM transcripts WHERE id=$t", ("$t", tid)).FirstOrDefault();
            if (t == null) return Results.NotFound();
            var file = Safe(raw, (string)t["path"]!);
            if (file == null || !File.Exists(file)) return Results.Problem("transcript file missing from the export");
            var n = Math.Clamp(count ?? 200, 1, 1000);
            var filter = all == 1 ? "" : "AND visible=1";
            var rows = before is { } b
                ? Rows(db, $"SELECT id, line, offset, length, type, ts FROM records WHERE tid=$t AND line<$b {filter} ORDER BY line DESC LIMIT $n",
                    ("$t", tid), ("$b", b), ("$n", n)).AsEnumerable().Reverse().ToList()
                : Rows(db, $"SELECT id, line, offset, length, type, ts FROM records WHERE tid=$t AND line>=$f {filter} ORDER BY line LIMIT $n",
                    ("$t", tid), ("$f", from ?? 1), ("$n", n));
            var output = new JsonArray();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                foreach (var r in rows)
                {
                    var id = (long)r["id"]!;
                    var length = (int)(long)r["length"]!;
                    var bytes = new byte[length];
                    fs.Seek((long)r["offset"]!, SeekOrigin.Begin);
                    fs.ReadExactly(bytes);
                    JsonNode? node;
                    try
                    {
                        node = JsonNode.Parse(bytes);
                    }
                    catch (JsonException)
                    {
                        node = new JsonObject { ["type"] = "(not JSON)", ["text"] = Encoding.UTF8.GetString(bytes, 0, Math.Min(length, 2000)) };
                    }
                    output.Add(new JsonObject
                    {
                        ["id"] = id, ["line"] = (long)r["line"]!, ["type"] = (string?)r["type"], ["ts"] = (string?)r["ts"],
                        ["bytes"] = length,
                        ["record"] = Slim(node, id),
                    });
                }
            }
            return Results.Text(output.ToJsonString(), "application/json");
        });

        app.MapGet("/api/record/{id:long}", (long id) =>
        {
            using var db = Open();
            var r = Rows(db, "SELECT r.offset, r.length, t.path FROM records r JOIN transcripts t ON t.id=r.tid WHERE r.id=$i", ("$i", id)).FirstOrDefault();
            if (r == null || Safe(raw, (string)r["path"]!) is not { } file) return Results.NotFound();
            return Results.Bytes(JsonLines.ReadAt(file, (long)r["offset"]!, (int)(long)r["length"]!), "application/json");
        });

        app.MapGet("/api/record/{id:long}/image/{n:int}", (long id, int n) =>
        {
            using var db = Open();
            var r = Rows(db, "SELECT r.offset, r.length, t.path FROM records r JOIN transcripts t ON t.id=r.tid WHERE r.id=$i", ("$i", id)).FirstOrDefault();
            if (r == null || Safe(raw, (string)r["path"]!) is not { } file) return Results.NotFound();
            var node = JsonNode.Parse(JsonLines.ReadAt(file, (long)r["offset"]!, (int)(long)r["length"]!));
            var i = 0;
            foreach (var img in Images(node))
            {
                if (i++ != n) continue;
                var src = img["source"]!;
                if (Text(src["data"]) is not { } b64) return Results.NotFound();
                return Results.Bytes(Convert.FromBase64String(b64), Text(src["media_type"]) ?? "application/octet-stream");
            }
            return Results.NotFound();
        });

        // ------------------------------------------------------------ search
        app.MapGet("/api/search", (string? q, string? session, int? limit) =>
        {
            var query = FtsQuery(q);
            if (query == null) return Results.Json(Array.Empty<object>());
            using var db = Open();
            var n = Math.Clamp(limit ?? 200, 1, 1000);
            var sessionFilter = string.IsNullOrEmpty(session) ? "" : "AND t.session_key=$s";
            try
            {
                var hits = Rows(db, $"""
                    SELECT 'record' kind, f.ref ref, snippet(fts, 0, char(1), char(2), ' … ', 20) snippet,
                           r.line, r.type, r.ts, r.tid, t.kind tkind, t.session_key, s.title, s.archived, s.deleted
                    FROM fts f JOIN records r ON r.id=f.ref JOIN transcripts t ON t.id=r.tid
                    LEFT JOIN sessions s ON s.key=t.session_key
                    WHERE fts MATCH $q AND f.kind='record' {sessionFilter}
                    ORDER BY rank LIMIT $n
                    """, ("$q", query), ("$n", n), ("$s", session));
                if (string.IsNullOrEmpty(session))
                {
                    hits.AddRange(Rows(db, """
                        SELECT 'memory' kind, f.ref ref, snippet(fts, 0, char(1), char(2), ' … ', 20) snippet,
                               m.path, m.name, m.project
                        FROM fts f JOIN memory m ON m.id=f.ref WHERE fts MATCH $q AND f.kind='memory'
                        ORDER BY rank LIMIT 50
                        """, ("$q", query)));
                }
                return Results.Json(hits);
            }
            catch (SqliteException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        // ------------------------------------------------------------ memory, files, folders
        app.MapGet("/api/memory", () =>
        {
            using var db = Open();
            return Results.Json(Rows(db, "SELECT id, project, path, name, bytes, mtime FROM memory ORDER BY project, name"));
        });

        app.MapGet("/api/tree", (string? path) =>
        {
            var dir = Safe(raw, path ?? "");
            if (dir == null || !Directory.Exists(dir)) return Results.NotFound();
            var info = new DirectoryInfo(dir);
            var dirs = info.EnumerateDirectories().OrderBy(d => d.Name).Take(5000)
                .Select(d => new { name = d.Name, path = Rel(raw, d.FullName), dir = true, bytes = (long?)null, mtime = d.LastWriteTimeUtc });
            var files = info.EnumerateFiles().OrderBy(f => f.Name).Take(5000)
                .Select(f => new { name = f.Name, path = Rel(raw, f.FullName), dir = false, bytes = (long?)f.Length, mtime = f.LastWriteTimeUtc });
            return Results.Json(new { path = Rel(raw, dir), entries = dirs.Concat(files) });
        });

        app.MapGet("/api/raw", (string path, int? download) =>
        {
            var file = Safe(raw, path);
            if (file == null || !File.Exists(file)) return Results.NotFound();
            return Serve(file, download == 1, types);
        });

        app.MapGet("/api/local", (string path, int? download) =>
        {
            using var db = Open();
            var known = Rows(db, "SELECT 1 x FROM touched WHERE path=$p UNION SELECT 1 FROM backups WHERE path=$p LIMIT 1", ("$p", path)).Count > 0;
            if (!known) return Results.BadRequest(new { error = "only a file a session touched can be opened" });
            if (!SafeExists(path)) return Results.NotFound(new { error = "the file no longer exists at that path" });
            return Serve(path, download == 1, types);
        });

        var url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"viewer: {url}  (export {folder}; Ctrl+C stops it)");
        if (openBrowser)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No browser to open: the URL above is enough.
            }
        }
        await app.RunAsync();
    }

    // ---------------------------------------------------------------- helpers

    static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name)
        ?? throw new InvalidOperationException($"embedded {name} missing");

    static IResult Serve(string file, bool download, FileExtensionContentTypeProvider types)
    {
        var name = Path.GetFileName(file);
        var ext = Path.GetExtension(file);
        if (!types.TryGetContentType(file, out var type) || type.StartsWith("text/", StringComparison.Ordinal))
            type = LooksText(file) ? "text/plain; charset=utf-8" : (type ?? "application/octet-stream");
        // Never let an exported HTML or SVG page run in the viewer's origin: show it as text.
        if (!download && (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)))
            type = "text/plain; charset=utf-8";
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return download ? Results.File(stream, "application/octet-stream", name) : Results.Stream(stream, type);
    }

    static bool LooksText(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var buf = new byte[4096];
            var n = fs.Read(buf, 0, buf.Length);
            return Array.IndexOf(buf, (byte)0, 0, n) < 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A path inside root, or null when the request tries to leave it.</summary>
    static string? Safe(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    static bool SafeExists(string? path)
    {
        try
        {
            return path != null && Path.IsPathFullyQualified(path) && File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/') is "." ? "" : Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>Turns what a person types into a safe FTS5 query: each word must appear (prefix with a trailing *),
    /// a "quoted phrase" must appear as written.</summary>
    static string? FtsQuery(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return null;
        var parts = new List<string>();
        var i = 0;
        while (i < q.Length)
        {
            if (char.IsWhiteSpace(q[i]))
            {
                i++;
                continue;
            }
            string token;
            var prefix = false;
            if (q[i] == '"')
            {
                var end = q.IndexOf('"', i + 1);
                token = end < 0 ? q[(i + 1)..] : q[(i + 1)..end];
                i = end < 0 ? q.Length : end + 1;
            }
            else
            {
                var end = i;
                while (end < q.Length && !char.IsWhiteSpace(q[end])) end++;
                token = q[i..end];
                i = end;
                if (token.EndsWith('*'))
                {
                    prefix = true;
                    token = token.TrimEnd('*');
                }
            }
            if (token.Length == 0) continue;
            parts.Add("\"" + token.Replace("\"", "\"\"") + "\"" + (prefix ? "*" : ""));
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>What the page needs to draw a record: long texts cut (the full record is one click away),
    /// images replaced by a URL, the duplicate tool output dropped.</summary>
    static JsonNode? Slim(JsonNode? node, long id)
    {
        if (node is not JsonObject o) return node;
        o.Remove("toolUseResult");
        var type = Text(o["type"]);
        var limit = type is "user" or "assistant" ? MaxText : MaxOtherText;
        var n = 0;
        foreach (var img in Images(o))
        {
            if (img["source"] is JsonObject src)
            {
                src.Remove("data");
                img["src"] = $"/api/record/{id}/image/{n}";
            }
            n++;
        }
        Cut(o, limit);
        return o;

        static void Cut(JsonNode? x, int limit)
        {
            switch (x)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > limit)
                        {
                            obj[key] = s[..limit];
                            obj[key + "__truncated"] = s.Length;
                        }
                        else
                        {
                            Cut(obj[key], limit);
                        }
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > limit) arr[i] = s[..limit];
                        else Cut(arr[i], limit);
                    }
                    break;
            }
        }
    }

    /// <summary>Every base64 image block in a record, in document order (the order the image URLs number them).</summary>
    static IEnumerable<JsonObject> Images(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (Text(o["type"]) == "image" && o["source"] is JsonObject s && Text(s["type"]) == "base64")
                    yield return o;
                foreach (var p in o.ToList())
                    foreach (var x in Images(p.Value))
                        yield return x;
                break;
            case JsonArray a:
                foreach (var item in a.ToList())
                    foreach (var x in Images(item))
                        yield return x;
                break;
        }
    }

    /// <summary>A node's string value, or null when it is missing or not a string.</summary>
    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static List<Dictionary<string, object?>> Rows(SqliteConnection db, string sql, params (string, object?)[] values)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (name, value) in values) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = c.ExecuteReader();
        var list = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>(r.FieldCount);
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }
}
