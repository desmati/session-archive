using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SessionArchive;

/// <summary>Builds &lt;folder&gt;/index.db from &lt;folder&gt;/raw: sessions, every transcript line's position,
/// full-text search, the files each session touched, file-history versions, memory and each session's folders.
/// The index points into raw/; the viewer reads the conversation itself from the copied transcripts.</summary>
public sealed partial class Indexer(string folder)
{
    const int MaxRecordText = 40_000, MaxToolInput = 4_000, MaxToolResult = 8_000, MaxOther = 3_000;
    static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
        { "Read", "Write", "Edit", "MultiEdit", "NotebookEdit", "NotebookRead" };
    static readonly HashSet<string> SearchTools = new(StringComparer.Ordinal) { "Glob", "Grep" };
    static readonly HashSet<string> VisibleTypes = new(StringComparer.Ordinal) { "user", "assistant", "system", "attachment" };

    readonly string raw = Path.Combine(folder, "raw");

    sealed class TranscriptStats
    {
        public long Id;
        public string Sid = "", Kind = "", Project = "", Path = "";
        public string? ParentSid, AgentId, Meta, Title, AgentName, LastPrompt, FirstPrompt, Cwd, Branch, FirstTs, LastTs;
        public long Lines, Bytes;
        public int UserMsgs, AssistantMsgs, ToolCalls;
        public readonly Dictionary<(string Path, string Op), (int Count, string? First, string? Last, long Line)> Touched = new();
        public readonly Dictionary<(string Path, string Backup), (int Version, string? Time)> Backups = new();
    }

    public void Run()
    {
        if (!Directory.Exists(raw)) throw new UserError($"{raw} does not exist: run an export first");
        var watch = Stopwatch.StartNew();
        var dbPath = Path.Combine(folder, "index.db");
        var tmpPath = dbPath + ".building";
        if (File.Exists(tmpPath)) File.Delete(tmpPath);
        SqliteConnection.ClearAllPools();

        using (var db = new SqliteConnection($"Data Source={tmpPath};Pooling=False"))
        {
            db.Open();
            Exec(db, """
                PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA page_size=8192;
                CREATE TABLE info(key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE transcripts(id INTEGER PRIMARY KEY, path TEXT, sid TEXT, kind TEXT, parent_sid TEXT,
                  agent_id TEXT, meta TEXT, project TEXT, lines INT, bytes INT, first_ts TEXT, last_ts TEXT, title TEXT,
                  agent_name TEXT, last_prompt TEXT, first_prompt TEXT, cwd TEXT, git_branch TEXT, user_msgs INT,
                  assistant_msgs INT, tool_calls INT, session_key TEXT);
                CREATE TABLE records(id INTEGER PRIMARY KEY, tid INT, line INT, offset INT, length INT, type TEXT,
                  uuid TEXT, ts TEXT, visible INT);
                CREATE TABLE sessions(key TEXT PRIMARY KEY, kind TEXT, title TEXT, sid TEXT, cwd TEXT, project TEXT,
                  archived INT, deleted INT, starred INT, created TEXT, last_activity TEXT, model TEXT, forked_from TEXT,
                  meta_path TEXT, main_tid INT, first_prompt TEXT, user_msgs INT, assistant_msgs INT, tool_calls INT,
                  transcripts INT, subagents INT, bytes INT);
                CREATE TABLE session_dirs(session_key TEXT, label TEXT, path TEXT, files INT, bytes INT);
                CREATE TABLE touched(session_key TEXT, tid INT, path TEXT, op TEXT, count INT, first_ts TEXT,
                  last_ts TEXT, first_line INT);
                CREATE TABLE backups(session_key TEXT, sid TEXT, path TEXT, backup TEXT, version INT, time TEXT,
                  present INT);
                CREATE TABLE memory(id INTEGER PRIMARY KEY, project TEXT, path TEXT, name TEXT, bytes INT, mtime TEXT);
                CREATE VIRTUAL TABLE fts USING fts5(text, kind UNINDEXED, ref UNINDEXED,
                  tokenize='unicode61 remove_diacritics 2');
                """);

            var transcripts = IndexTranscripts(db);
            var sessions = BuildSessions(db, transcripts);
            IndexSessionDirs(db, sessions);
            IndexMemory(db);
            WriteInfo(db, watch);

            Console.WriteLine("index: building lookups");
            Exec(db, """
                CREATE INDEX records_tid_line ON records(tid, line);
                CREATE INDEX transcripts_session ON transcripts(session_key);
                CREATE INDEX touched_session ON touched(session_key);
                CREATE INDEX touched_path ON touched(path);
                CREATE INDEX backups_session ON backups(session_key);
                CREATE INDEX dirs_session ON session_dirs(session_key);
                INSERT INTO fts(fts) VALUES('optimize');
                """);
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmpPath, dbPath, overwrite: true);
        Console.WriteLine($"index: written in {watch.Elapsed.TotalSeconds:N0} s ({new FileInfo(dbPath).Length / 1048576.0:N0} MB)");
    }

    // ---------------------------------------------------------------- transcripts

    List<TranscriptStats> IndexTranscripts(SqliteConnection db)
    {
        var projects = Path.Combine(raw, "claude-home", "projects");
        var list = new List<TranscriptStats>();
        if (!Directory.Exists(projects)) return list;
        var files = Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"index: {files.Count:N0} transcripts");

        using var insRecord = Prepare(db, "INSERT INTO records(id,tid,line,offset,length,type,uuid,ts,visible) VALUES($id,$t,$l,$o,$n,$ty,$u,$ts,$v)");
        using var insFts = Prepare(db, "INSERT INTO fts(text,kind,ref) VALUES($x,'record',$r)");
        long recordId = 0, tid = 0, totalLines = 0;
        var done = 0;
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Rel(file);
            var stats = Describe(file, rel, ++tid);
            using var tx = db.BeginTransaction();
            insRecord.Transaction = tx;
            insFts.Transaction = tx;
            long line = 0;
            foreach (var (offset, length, bytes) in JsonLines.Read(file))
            {
                line++;
                stats.Bytes += length + 1;
                string? type = null, uuid = null, ts = null, text = null;
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    var root = doc.RootElement;
                    type = Str(root, "type");
                    uuid = Str(root, "uuid");
                    ts = Str(root, "timestamp");
                    text = Observe(stats, root, type, ts, line);
                }
                catch (JsonException)
                {
                    type = "(not JSON)";
                }
                if (ts != null)
                {
                    stats.FirstTs ??= ts;
                    stats.LastTs = ts;
                }
                recordId++;
                Bind(insRecord, ("$id", recordId), ("$t", tid), ("$l", line), ("$o", offset), ("$n", length),
                    ("$ty", type), ("$u", uuid), ("$ts", ts), ("$v", type != null && VisibleTypes.Contains(type) ? 1 : 0));
                insRecord.ExecuteNonQuery();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Bind(insFts, ("$x", text), ("$r", recordId));
                    insFts.ExecuteNonQuery();
                }
            }
            stats.Lines = line;
            totalLines += line;
            tx.Commit();
            list.Add(stats);
            if (++done % 25 == 0 || done == files.Count)
                Console.WriteLine($"  {done:N0} / {files.Count:N0} transcripts, {totalLines:N0} lines");
        }

        using var insT = Prepare(db, """
            INSERT INTO transcripts(id,path,sid,kind,parent_sid,agent_id,meta,project,lines,bytes,first_ts,last_ts,title,
              agent_name,last_prompt,first_prompt,cwd,git_branch,user_msgs,assistant_msgs,tool_calls)
            VALUES($id,$p,$s,$k,$ps,$a,$m,$pr,$l,$b,$f,$la,$ti,$an,$lp,$fp,$c,$g,$u,$as,$tc)
            """);
        using (var tx = db.BeginTransaction())
        {
            insT.Transaction = tx;
            foreach (var t in list)
            {
                Bind(insT, ("$id", t.Id), ("$p", t.Path), ("$s", t.Sid), ("$k", t.Kind), ("$ps", t.ParentSid),
                    ("$a", t.AgentId), ("$m", t.Meta), ("$pr", t.Project), ("$l", t.Lines), ("$b", t.Bytes),
                    ("$f", t.FirstTs), ("$la", t.LastTs), ("$ti", t.Title), ("$an", t.AgentName), ("$lp", t.LastPrompt),
                    ("$fp", t.FirstPrompt), ("$c", t.Cwd), ("$g", t.Branch), ("$u", t.UserMsgs),
                    ("$as", t.AssistantMsgs), ("$tc", t.ToolCalls));
                insT.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return list;
    }

    /// <summary>Where a transcript sits says what it is: projects/&lt;project&gt;/&lt;sid&gt;.jsonl is a session;
    /// projects/&lt;project&gt;/&lt;sid&gt;/subagents/agent-&lt;id&gt;.jsonl is one of its sub-agents.</summary>
    TranscriptStats Describe(string file, string rel, long id)
    {
        var parts = rel.Split('/'); // claude-home/projects/<project>/...
        var stats = new TranscriptStats { Id = id, Path = rel, Project = parts.Length > 2 ? parts[2] : "" };
        var name = Path.GetFileNameWithoutExtension(file);
        if (parts.Length == 4)
        {
            stats.Kind = "main";
            stats.Sid = name;
        }
        else
        {
            stats.Kind = parts.Contains("subagents") ? "subagent" : "other";
            stats.Sid = name;
            stats.ParentSid = parts.Length > 3 ? parts[3] : null;
            stats.AgentId = name.StartsWith("agent-", StringComparison.Ordinal) ? name[6..] : name;
            var meta = Path.Combine(Path.GetDirectoryName(file)!, name + ".meta.json");
            if (File.Exists(meta)) stats.Meta = File.ReadAllText(meta);
        }
        return stats;
    }

    /// <summary>Reads what the index needs from one record and returns its searchable text.</summary>
    static string? Observe(TranscriptStats s, JsonElement root, string? type, string? ts, long line)
    {
        switch (type)
        {
            case "custom-title":
                s.Title = Str(root, "customTitle") ?? s.Title;
                return null;
            case "agent-name":
                s.AgentName = Str(root, "agentName") ?? s.AgentName;
                return null;
            case "last-prompt":
                s.LastPrompt = Str(root, "lastPrompt") ?? s.LastPrompt;
                return null;
            case "file-history-snapshot":
                if (root.TryGetProperty("snapshot", out var snap) && snap.TryGetProperty("trackedFileBackups", out var tracked)
                    && tracked.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in tracked.EnumerateObject()) AddBackup(s, p.Name, p.Value);
                }
                return null;
            case "file-history-delta":
                if (root.TryGetProperty("backup", out var backup) && Str(root, "trackingPath") is { } tp)
                    AddBackup(s, tp, backup);
                return null;
            case "user" or "assistant":
                break;
            case "system":
                return Cap(Str(root, "content"), MaxOther);
            case "attachment":
                if (!root.TryGetProperty("attachment", out var att)) return null;
                var sb0 = new StringBuilder();
                CollectStrings(att, sb0, MaxOther);
                return sb0.ToString();
            default:
                return null;
        }

        s.Cwd ??= Str(root, "cwd");
        s.Branch ??= Str(root, "gitBranch");
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        var isMeta = root.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True;
        var sb = new StringBuilder();
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? "";
            sb.Append(text);
            if (type == "user" && !isMeta) NoteUserText(s, text);
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var sawText = false;
            foreach (var block in content.EnumerateArray())
            {
                if (sb.Length > MaxRecordText) break;
                switch (Str(block, "type"))
                {
                    case "text":
                        var text = Str(block, "text") ?? "";
                        sb.Append(text).Append('\n');
                        sawText = true;
                        if (type == "user" && !isMeta) NoteUserText(s, text);
                        break;
                    case "thinking":
                        sb.Append(Str(block, "thinking")).Append('\n');
                        break;
                    case "tool_use":
                        s.ToolCalls++;
                        var name = Str(block, "name") ?? "";
                        sb.Append(name).Append(' ');
                        if (block.TryGetProperty("input", out var input))
                        {
                            sb.Append(Cap(input.GetRawText(), MaxToolInput)).Append('\n');
                            NoteTouched(s, name, input, ts, line);
                        }
                        break;
                    case "tool_result":
                        if (block.TryGetProperty("content", out var rc))
                        {
                            if (rc.ValueKind == JsonValueKind.String) sb.Append(Cap(rc.GetString(), MaxToolResult));
                            else if (rc.ValueKind == JsonValueKind.Array)
                                foreach (var b in rc.EnumerateArray())
                                    if (Str(b, "type") == "text") sb.Append(Cap(Str(b, "text"), MaxToolResult));
                            sb.Append('\n');
                        }
                        break;
                }
            }
            if (sawText && type == "assistant") s.AssistantMsgs++;
        }
        return Cap(sb.ToString(), MaxRecordText);
    }

    static void NoteUserText(TranscriptStats s, string text)
    {
        var t = text.TrimStart();
        if (t.Length == 0 || t.StartsWith('<')) return; // command wrappers, system reminders
        s.UserMsgs++;
        s.FirstPrompt ??= Cap(t, 300);
    }

    static void NoteTouched(TranscriptStats s, string tool, JsonElement input, string? ts, long line)
    {
        string? path = null, op = null;
        if (FileTools.Contains(tool))
        {
            path = Str(input, "file_path") ?? Str(input, "notebook_path");
            op = tool;
        }
        else if (SearchTools.Contains(tool))
        {
            path = Str(input, "path");
            op = tool;
        }
        if (string.IsNullOrWhiteSpace(path) || op == null) return;
        var key = (path, op);
        s.Touched[key] = s.Touched.TryGetValue(key, out var v)
            ? (v.Count + 1, v.First, ts ?? v.Last, v.Line)
            : (1, ts, ts, line);
    }

    static void AddBackup(TranscriptStats s, string path, JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object) return;
        var name = Str(b, "backupFileName");
        if (name == null) return; // the file did not exist yet at that point
        var version = b.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
        s.Backups[(path, name)] = (version, Str(b, "backupTime"));
    }

    // ---------------------------------------------------------------- sessions

    sealed record SessionRow(string Key, string Kind)
    {
        public string? Title, Sid, Cwd, Project, Created, LastActivity, Model, ForkedFrom, MetaPath, FirstPrompt;
        public bool Archived, Deleted, Starred;
        public readonly List<TranscriptStats> Transcripts = [];
        public readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
    }

    List<SessionRow> BuildSessions(SqliteConnection db, List<TranscriptStats> transcripts)
    {
        var bySid = transcripts.Where(t => t.Kind == "main").GroupBy(t => t.Sid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var sessions = new List<SessionRow>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The desktop app's records: one per session in its sidebar, archived ones included.
        var desktop = Path.Combine(raw, "desktop");
        var archivedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(desktop))
        {
            foreach (var idx in Directory.EnumerateFiles(desktop, "archived-sessions.idx", SearchOption.AllDirectories))
            {
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllBytes(idx));
                    if (d.RootElement.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.Array)
                        foreach (var e in a.EnumerateArray())
                            if (e.GetString() is { } s) archivedIds.Add(s);
                }
                catch (JsonException) { }
            }
            foreach (var f in Directory.EnumerateFiles(desktop, "deleted_*", SearchOption.AllDirectories))
                deletedIds.Add(Path.GetFileName(f)["deleted_".Length..]);

            foreach (var f in Directory.EnumerateFiles(desktop, "*.json", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(f).StartsWith("local_", StringComparison.Ordinal)) continue;
                JsonElement r;
                try
                {
                    r = JsonDocument.Parse(File.ReadAllBytes(f)).RootElement;
                }
                catch (JsonException)
                {
                    continue;
                }
                var key = Str(r, "sessionId") ?? Path.GetFileNameWithoutExtension(f);
                var row = new SessionRow(key, Rel(f).Contains("local-agent-mode", StringComparison.Ordinal) ? "cowork" : "desktop")
                {
                    Title = Str(r, "title"),
                    Sid = Str(r, "cliSessionId"),
                    Cwd = Str(r, "cwd"),
                    Model = Str(r, "model"),
                    ForkedFrom = Str(r, "forkedFromSessionId"),
                    MetaPath = Rel(f),
                    Created = Millis(r, "createdAt"),
                    LastActivity = Millis(r, "lastActivityAt"),
                    Archived = (r.TryGetProperty("isArchived", out var ia) && ia.ValueKind == JsonValueKind.True) || archivedIds.Contains(key),
                    Starred = r.TryGetProperty("isStarred", out var st) && st.ValueKind == JsonValueKind.True,
                };
                row.Deleted = deletedIds.Contains(key) || deletedIds.Contains(key.Replace("local_", ""));
                row.Ids.Add(key);
                row.Ids.Add(key.Replace("local_", ""));
                foreach (var sid in new[] { Str(r, "cliSessionId"), Str(r, "lastAssistantCliSessionId") })
                {
                    if (sid == null) continue;
                    row.Ids.Add(sid);
                    if (bySid.TryGetValue(sid, out var ts) && claimed.Add(sid)) row.Transcripts.AddRange(ts);
                }
                sessions.Add(row);
            }
        }

        // Every transcript the desktop does not list (older, deleted, terminal or headless sessions) is a session too.
        foreach (var (sid, ts) in bySid)
        {
            if (claimed.Contains(sid)) continue;
            var row = new SessionRow(sid, "cli") { Sid = sid };
            row.Ids.Add(sid);
            row.Transcripts.AddRange(ts);
            var released = Path.Combine(raw, "claude-home", "projects", ts[0].Project, sid + ".desktop-released.json");
            if (File.Exists(released))
            {
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllBytes(released));
                    row.Deleted = Str(d.RootElement, "reason") == "delete";
                }
                catch (JsonException) { }
            }
            row.Deleted |= deletedIds.Contains(sid);
            sessions.Add(row);
        }

        // Sub-agents belong to the session whose transcript started them.
        var keyBySid = new Dictionary<string, SessionRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
            foreach (var t in s.Transcripts)
                keyBySid[t.Sid] = s;
        var orphans = new SessionRow("(unattached sub-agents)", "cli") { Title = "Sub-agents whose session is gone" };
        foreach (var t in transcripts.Where(t => t.Kind != "main"))
        {
            if (t.ParentSid != null && keyBySid.TryGetValue(t.ParentSid, out var s)) s.Transcripts.Add(t);
            else orphans.Transcripts.Add(t);
        }
        if (orphans.Transcripts.Count > 0) sessions.Add(orphans);

        using var insS = Prepare(db, """
            INSERT INTO sessions(key,kind,title,sid,cwd,project,archived,deleted,starred,created,last_activity,model,
              forked_from,meta_path,main_tid,first_prompt,user_msgs,assistant_msgs,tool_calls,transcripts,subagents,bytes)
            VALUES($k,$kind,$t,$s,$c,$p,$a,$d,$st,$cr,$la,$m,$f,$mp,$mt,$fp,$u,$as,$tc,$n,$sa,$b)
            """);
        using var setKey = Prepare(db, "UPDATE transcripts SET session_key=$k WHERE id=$id");
        using var insTouched = Prepare(db, "INSERT INTO touched VALUES($k,$t,$p,$o,$n,$f,$l,$ln)");
        using var insBackup = Prepare(db, "INSERT INTO backups VALUES($k,$s,$p,$b,$v,$t,$pr)");
        using var tx = db.BeginTransaction();
        foreach (var c in new[] { insS, setKey, insTouched, insBackup }) c.Transaction = tx;
        foreach (var s in sessions)
        {
            var mains = s.Transcripts.Where(t => t.Kind == "main").OrderBy(t => t.FirstTs).ToList();
            var main = mains.LastOrDefault();
            s.Title ??= main?.Title ?? main?.AgentName ?? Cap(main?.FirstPrompt, 80) ?? Cap(main?.LastPrompt, 80) ?? s.Key;
            s.FirstPrompt = mains.Select(t => t.FirstPrompt).FirstOrDefault(p => p != null);
            s.Cwd ??= main?.Cwd;
            s.Project = main?.Project ?? s.Transcripts.FirstOrDefault()?.Project;
            s.Created ??= mains.Select(t => t.FirstTs).FirstOrDefault(x => x != null);
            var last = s.Transcripts.Select(t => t.LastTs).Where(x => x != null).Max(StringComparer.Ordinal);
            if (s.LastActivity == null || (last != null && string.CompareOrdinal(last, s.LastActivity) > 0)) s.LastActivity = last;
            Bind(insS, ("$k", s.Key), ("$kind", s.Kind), ("$t", s.Title), ("$s", s.Sid), ("$c", s.Cwd), ("$p", s.Project),
                ("$a", s.Archived ? 1 : 0), ("$d", s.Deleted ? 1 : 0), ("$st", s.Starred ? 1 : 0), ("$cr", s.Created),
                ("$la", s.LastActivity), ("$m", s.Model), ("$f", s.ForkedFrom), ("$mp", s.MetaPath), ("$mt", main?.Id),
                ("$fp", s.FirstPrompt), ("$u", mains.Sum(t => t.UserMsgs)), ("$as", mains.Sum(t => t.AssistantMsgs)),
                ("$tc", s.Transcripts.Sum(t => t.ToolCalls)), ("$n", mains.Count),
                ("$sa", s.Transcripts.Count(t => t.Kind != "main")), ("$b", s.Transcripts.Sum(t => t.Bytes)));
            insS.ExecuteNonQuery();
            foreach (var t in s.Transcripts)
            {
                Bind(setKey, ("$k", s.Key), ("$id", t.Id));
                setKey.ExecuteNonQuery();
                foreach (var ((path, op), v) in t.Touched)
                {
                    Bind(insTouched, ("$k", s.Key), ("$t", t.Id), ("$p", path), ("$o", op), ("$n", v.Count),
                        ("$f", v.First), ("$l", v.Last), ("$ln", v.Line));
                    insTouched.ExecuteNonQuery();
                }
                foreach (var ((recorded, backup), v) in t.Backups)
                {
                    // File history names a file relative to the session's folder; tool calls use the full path.
                    var path = Absolute(recorded, t.Cwd ?? s.Cwd);
                    var rel = $"claude-home/file-history/{t.Sid}/{backup}";
                    var present = File.Exists(Path.Combine(raw, rel.Replace('/', '\\')));
                    Bind(insBackup, ("$k", s.Key), ("$s", t.Sid), ("$p", path), ("$b", rel), ("$v", v.Version),
                        ("$t", v.Time), ("$pr", present ? 1 : 0));
                    insBackup.ExecuteNonQuery();
                }
            }
        }
        tx.Commit();
        Console.WriteLine($"index: {sessions.Count:N0} sessions ({sessions.Count(s => s.Archived)} archived, {sessions.Count(s => s.Deleted)} deleted)");
        return sessions;
    }

    // ---------------------------------------------------------------- folders, memory

    /// <summary>Any folder in the export named after a session (temp\&lt;x&gt;\&lt;sid&gt; with its scratchpad and
    /// task output, file-history\&lt;sid&gt;, uploads, the transcript's own folder of tool results and sub-agents...)
    /// is that session's. Searched four levels deep under each source.</summary>
    void IndexSessionDirs(SqliteConnection db, List<SessionRow> sessions)
    {
        var owner = new Dictionary<string, SessionRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            foreach (var id in s.Ids) owner.TryAdd(id, s);
            foreach (var t in s.Transcripts.Where(t => t.Kind == "main")) owner.TryAdd(t.Sid, s);
        }
        using var ins = Prepare(db, "INSERT INTO session_dirs VALUES($k,$l,$p,$f,$b)");
        using var tx = db.BeginTransaction();
        ins.Transaction = tx;
        var found = 0;
        foreach (var sourceDir in Directory.EnumerateDirectories(raw))
        {
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((sourceDir, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(dir).ToList();
                }
                catch (IOException)
                {
                    continue;
                }
                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (owner.TryGetValue(name, out var s) && IdLike().IsMatch(name))
                    {
                        long files = 0, bytes = 0;
                        foreach (var f in new DirectoryInfo(child).EnumerateFiles("*", SearchOption.AllDirectories))
                        {
                            files++;
                            bytes += f.Length;
                        }
                        var rel = Rel(child);
                        Bind(ins, ("$k", s.Key), ("$l", Label(rel)), ("$p", rel), ("$f", files), ("$b", bytes));
                        ins.ExecuteNonQuery();
                        found++;
                    }
                    else if (depth < 4)
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
        }
        tx.Commit();
        Console.WriteLine($"index: {found:N0} session folders");

        static string Label(string rel)
        {
            var p = rel.Split('/');
            return p[0] switch
            {
                "temp" => "Scratchpad, task output and temp files",
                "claude-home" when p.Length > 1 && p[1] == "projects" => "Tool results and sub-agents",
                "claude-home" when p.Length > 1 && p[1] == "file-history" => "File history (versions of edited files)",
                "claude-home" when p.Length > 1 => $"{p[1]} (Claude Code)",
                "desktop" => "Desktop app records",
                _ => p[0],
            };
        }
    }

    void IndexMemory(SqliteConnection db)
    {
        var projects = Path.Combine(raw, "claude-home", "projects");
        if (!Directory.Exists(projects)) return;
        using var ins = Prepare(db, "INSERT INTO memory(id,project,path,name,bytes,mtime) VALUES($id,$p,$r,$n,$b,$m)");
        using var fts = Prepare(db, "INSERT INTO fts(text,kind,ref) VALUES($x,'memory',$r)");
        using var tx = db.BeginTransaction();
        ins.Transaction = tx;
        fts.Transaction = tx;
        long id = 0;
        foreach (var project in Directory.EnumerateDirectories(projects))
        {
            var mem = Path.Combine(project, "memory");
            if (!Directory.Exists(mem)) continue;
            foreach (var f in Directory.EnumerateFiles(mem, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(f);
                id++;
                Bind(ins, ("$id", id), ("$p", Path.GetFileName(project)), ("$r", Rel(f)), ("$n", info.Name),
                    ("$b", info.Length), ("$m", info.LastWriteTimeUtc.ToString("O")));
                ins.ExecuteNonQuery();
                if (info.Length < 1_000_000)
                {
                    Bind(fts, ("$x", File.ReadAllText(f)), ("$r", id));
                    fts.ExecuteNonQuery();
                }
            }
        }
        tx.Commit();
        Console.WriteLine($"index: {id:N0} memory files");
    }

    void WriteInfo(SqliteConnection db, Stopwatch watch)
    {
        var manifest = Path.Combine(folder, "manifest.json");
        Exec(db, "INSERT INTO info VALUES('indexedAt', $v)", ("$v", DateTimeOffset.Now.ToString("O")));
        Exec(db, "INSERT INTO info VALUES('manifest', $v)", ("$v", File.Exists(manifest) ? File.ReadAllText(manifest) : null));
        Exec(db, "INSERT INTO info VALUES('version', $v)", ("$v", typeof(Indexer).Assembly.GetName().Version?.ToString(3)));
    }

    // ---------------------------------------------------------------- helpers

    [GeneratedRegex("^(local_)?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex IdLike();

    string Rel(string full) => Path.GetRelativePath(raw, full).Replace('\\', '/');

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static string? Millis(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : null;

    static string Absolute(string path, string? cwd)
    {
        try
        {
            return Path.IsPathRooted(path) || cwd == null ? path : Path.GetFullPath(Path.Combine(cwd, path));
        }
        catch (Exception)
        {
            return path;
        }
    }

    static string? Cap(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

    static void CollectStrings(JsonElement e, StringBuilder sb, int max)
    {
        if (sb.Length >= max) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(Cap(e.GetString(), max - sb.Length)).Append(' ');
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) CollectStrings(p.Value, sb, max);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) CollectStrings(x, sb, max);
                break;
        }
    }

    static SqliteCommand Prepare(SqliteConnection db, string sql)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        return c;
    }

    static void Bind(SqliteCommand c, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (c.Parameters.Contains(name)) c.Parameters[name].Value = value ?? DBNull.Value;
            else c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    static void Exec(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        using var c = Prepare(db, sql);
        Bind(c, values);
        c.ExecuteNonQuery();
    }
}

/// <summary>Reads a JSONL file line by line with each line's byte offset, without loading the file.</summary>
public static class JsonLines
{
    public static IEnumerable<(long Offset, int Length, ReadOnlyMemory<byte> Bytes)> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        var buf = new byte[1 << 20];
        var line = new byte[1 << 16];
        var len = 0;
        long pos = 0, start = 0;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            var i = 0;
            while (i < n)
            {
                var j = Array.IndexOf(buf, (byte)'\n', i, n - i);
                var end = j < 0 ? n : j;
                var count = end - i;
                if (len + count > line.Length) Array.Resize(ref line, Math.Max(line.Length * 2, len + count));
                Buffer.BlockCopy(buf, i, line, len, count);
                len += count;
                if (j < 0)
                {
                    i = n;
                    break;
                }
                var l = len > 0 && line[len - 1] == '\r' ? len - 1 : len;
                if (l > 0) yield return (start, l, new ReadOnlyMemory<byte>(line, 0, l));
                start = pos + j + 1;
                len = 0;
                i = j + 1;
            }
            pos += n;
        }
        if (len > 0) yield return (start, len, new ReadOnlyMemory<byte>(line, 0, len));
    }

    public static byte[] ReadAt(string path, long offset, int length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[length];
        fs.ReadExactly(bytes);
        return bytes;
    }
}
