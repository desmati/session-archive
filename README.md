# session-archive

An unofficial archive and viewer for your local **Claude Code** and **Claude desktop app** sessions on Windows.

`session-archive export` copies everything Claude keeps about your sessions on your machine into one folder: every
conversation, including archived ones and ones the app no longer lists, plus their sub-agents, memory, scratchpads,
tool output, attachments and file history. `session-archive view` opens a local web page where you can list,
search, read and browse all of it.

> **Not affiliated with Anthropic.** This tool reads the files Claude Code and the Claude desktop app write to your
> disk. Those formats are internal and undocumented, and an update to Claude can change them at any time. If the
> viewer stops showing something, the copied files are still in the export, untouched.

## Requirements

- Windows 10 or 11. Other platforms keep these files elsewhere and are not supported yet.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download), to build it.

## Build and use

```powershell
git clone https://github.com/desmati/session-archive
cd session-archive
dotnet build -c Release src/SessionArchive
$sa = "src\SessionArchive\bin\Release\net10.0\session-archive.exe"

& $sa export "$env:USERPROFILE\SessionArchive"   # copy everything, then build the index
& $sa view   "$env:USERPROFILE\SessionArchive"   # http://127.0.0.1:5197, opens your browser
& $sa sources                                     # where it reads from, and what it leaves out
```

Run `export` as often as you like. It is **additive**: changed files are updated, new ones added, and nothing is ever
deleted from the export. Claude Code removes old sessions after a while (30 days by default, the `cleanupPeriodDays`
setting), so a regular export keeps sessions it would otherwise lose. `export <folder> --index-only` rebuilds only the
search index. `view <folder> --port <n>` picks another port.

## What is exported

The files are copied as they are into `<folder>\raw\<source>\`. Each source keeps its own folder layout.

| Source | Read from | Holds |
| --- | --- | --- |
| `claude-home` | `%USERPROFILE%\.claude` | Every transcript (`projects\<folder>\<session>.jsonl`) and its sub-agents, tool output saved as files (images included), **memory** (`projects\<folder>\memory`), file history (the versions Claude saved of files it edited), uploads, plans, scheduled tasks, skills, plugins, settings |
| `claude-json` | `%USERPROFILE%\.claude.json*` | Claude Code's global state |
| `desktop` | `%APPDATA%\Claude` | The desktop app's record of each session: title, archived or not, forks, model, deleted markers, Cowork, scheduled tasks, plus its git shadow and logs |
| `temp` | `%TEMP%\claude` | Each session's scratchpad (the scripts and files it made), background-task output, bundled skills, edit diffs |

Images and files pasted into a conversation are stored inside its transcript, and the viewer shows them.

`manifest.json` records what was copied, from where, and what failed. `exports\` keeps one such report per run.

## What is left out, on purpose

- **Your sign-in.** `~\.claude\.credentials.json` is never copied. In `.claude.json` and the desktop's `config.json`,
  every property named like a token, secret, password or API key is removed, and each scrubbed file lists what was
  removed under `_sessionArchiveScrubbed`.
- **The desktop app itself.** Its program files, VM images and browser caches hold no session data.
- **Files outside Claude's folders.** When a session read or wrote a file in one of your projects, the export **links
  it by its path** and does not copy it. The viewer's Files tab lists each one, and "open" shows the file as it is
  now. Claude's own file-history versions *are* copied.
- **Links.** Junctions and symbolic links are not followed. `manifest.json` names each one.

## Keep the export private

The export is a plain, unencrypted folder, and it is not scanned for secrets. Anything a session ever read or printed
is in it: file contents, command output, and possibly a credential. Treat it like the files it came from, and do not
upload or share it.

## The viewer

`view` serves the page on `127.0.0.1` only, and answers requests for no other host name, which protects it from DNS
rebinding. It never writes to the export. It serves only files inside `raw\`, and files a session touched (by their
recorded path). HTML and SVG from the export are shown as text, never run.

- **Sessions**, newest first. Filter by title, folder or id. Show or hide active, archived and deleted sessions, and
  ones not in the app: terminal and headless (`claude -p`) sessions, or ones the desktop no longer lists.
- **Search**, full text over every conversation and all memory: prompts, replies, thinking, tool calls and their
  output. Search for words, a `"quoted phrase"` or a `prefix*`. A hit opens the conversation at that line.
- **A session** has these tabs:
  - **Conversation**: prompts, replies, thinking, tool calls with their results, and images. It loads as you scroll
    and has find-in-session. **Show every record** adds the bookkeeping lines. Every record links to its raw JSON.
  - **Sub-agents**: each one's own transcript.
  - **Files**: every file the session touched, Claude's saved versions, and a link to the file now.
  - **Session folders**: the scratchpad, task output, tool output, file history and uploads.
  - **Memory**: the project's memory files.
  - **Details**: the app's record and every transcript file.
- **Memory** and **Everything exported** browse the whole export.

## How it works

`index.db` is a SQLite database with FTS5 full-text search. It holds the sessions and the byte position of every
transcript line. The viewer reads conversations straight from the copied `.jsonl` files, a page at a time, so it stays
fast on gigabytes of history. Search text is capped per record: tool input at 4 KB, tool output at 8 KB, a record at
40 KB. The full record is always one click away.

Sessions are put together from two sources:

- The desktop app's records give the title and the archived state, and point to a transcript.
- Every transcript nothing points to becomes a session of its own. These are terminal, headless or older sessions.

A folder anywhere in the export named after a session's id belongs to that session.

## License

[MIT](LICENSE).
