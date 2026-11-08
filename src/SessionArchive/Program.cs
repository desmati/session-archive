using SessionArchive;

const string usage = """
    session-archive - export every local Claude Code and Claude desktop session, and browse the export.

      session-archive export <folder> [--index-only]
          Copies everything Claude keeps on this machine into <folder>\raw (additive:
          a re-export updates changed files and never deletes old ones), then builds
          <folder>\index.db for the viewer. --index-only rebuilds the index alone.

      session-archive view <folder> [--port <n>] [--no-browser]
          Serves the viewer on http://127.0.0.1:<port> (default 5197) and opens it.

      session-archive sources
          Lists where the data is read from on this machine, and what is left out.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 64 : 0;
}

try
{
    switch (args[0])
    {
        case "export" when args.Length >= 2:
        {
            var folder = Path.GetFullPath(args[1]);
            var indexOnly = args.Contains("--index-only");
            Directory.CreateDirectory(folder);
            if (!indexOnly)
            {
                await new Exporter(folder, Sources.Discover()).RunAsync();
            }
            new Indexer(folder).Run();
            Console.WriteLine($"done: {folder}");
            Console.WriteLine($"browse it with: session-archive view \"{folder}\"");
            return 0;
        }
        case "view" when args.Length >= 2:
        {
            var folder = Path.GetFullPath(args[1]);
            var port = 5197;
            var i = Array.IndexOf(args, "--port");
            if (i > 0 && (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port)))
            {
                Console.Error.WriteLine("--port takes a number");
                return 64;
            }
            await Viewer.RunAsync(folder, port, openBrowser: !args.Contains("--no-browser"));
            return 0;
        }
        case "sources":
            foreach (var s in Sources.Discover())
            {
                var present = Directory.Exists(s.Root) || File.Exists(s.Root);
                Console.WriteLine($"{s.Name,-12} {s.Root}{(present ? "" : "  (missing)")}");
                Console.WriteLine($"{"",-12} {s.Description}");
            }
            Console.WriteLine();
            Console.WriteLine("Always left out: " + string.Join("; ", Sources.AlwaysExcluded));
            return 0;
        default:
            Console.Error.WriteLine(usage);
            return 64;
    }
}
catch (UserError e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

namespace SessionArchive
{
    public sealed class UserError(string message) : Exception(message);
}
