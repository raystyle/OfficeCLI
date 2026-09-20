// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Reflection;
using System.Text;

namespace OfficeCli.Help;

/// <summary>
/// Default help face (cli-docs 乙面第三节) rendered straight from the live
/// command tree. System.CommandLine 3.0-preview ships a sealed HelpAction with
/// no public HelpBuilder, so every help path (root `help`, `help &lt;cmd&gt;`,
/// the Program.cs help-flag rewrite) renders here instead of forwarding to
/// SCL's built-in layout. Section order per the family standard —
/// root/group form: header line, Usage, Commands, Global Options, Examples,
/// Environment Variables; leaf form: header line, Usage, Arguments, Options,
/// Examples, Global Options. Descriptions, defaults and arities come from the
/// same tree objects `--llms` renders: one source, no hand-maintained copy.
/// </summary>
internal static class HelpFace
{
    private const int TotalWidth = 100;

    /// <summary>Version from the assembly manifest (single source with
    /// --version/--llms), cut at the lineage suffix.</summary>
    private static string Version => (typeof(HelpFace).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    // ==================== shared facts (also used by --llms) ====================

    /// <summary>
    /// One-line blurb for tables: first sentence of a description, cut before
    /// any parenthetical, capped with an ellipsis. Shared with LlmsManual so
    /// the help Commands table and the --llms subcommand table can never
    /// disagree on how a command is summarized.
    /// </summary>
    internal static string Blurb(string? description, int cap = 90)
    {
        if (string.IsNullOrEmpty(description)) return "";
        var text = description.Trim();
        var nl = text.IndexOf('\n');
        if (nl > 0) text = text[..nl];
        var cut = text.IndexOfAny(['.', ':', ';', '(']);
        if (cut > 0) text = text[..cut];
        text = text.TrimEnd();
        return text.Length <= cap ? text : text[..cap].TrimEnd() + "…";
    }

    /// <summary>
    /// Common flags = option names shared by >= 4 non-internal subcommands,
    /// derived live (never curated). Shared with LlmsManual; drives the
    /// Global Options split in leaf help.
    /// </summary>
    internal static IReadOnlyList<string> DeriveCommonFlags(RootCommand root) => root.Subcommands
        .Where(c => !c.Name.StartsWith("__", StringComparison.Ordinal))
        .SelectMany(c => c.Options.Select(o => o.Name).Where(n => n is not null))
        .GroupBy(f => f, StringComparer.Ordinal)
        .Where(g => g.Count() >= 4)
        .Select(g => g.Key!)
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();

    // ==================== curated semantic sections ====================
    // Structural sections (commands, options, defaults) are tree-derived.
    // Examples are semantic content the tree cannot know; this registry is
    // the single curated copy — `--llms` renders the "root" entry verbatim
    // so help and manual can never drift apart.

    internal static IReadOnlyDictionary<string, string[]> Examples { get; } =
        new Dictionary<string, string[]>
        {
            ["root"] = new[]
            {
                "officecli create report.docx",
                "officecli add report.docx /body --type paragraph --prop text=\"Summary\" --prop style=Heading1",
                "officecli set data.xlsx '/Sheet1/A1' --prop value=Name --prop bold=true",
                "officecli get slides.pptx '/slide[1]' --depth 1 --json",
                "officecli query report.docx 'paragraph[style!=Normal]'",
                "officecli view deck.pptx html            # rendered snapshot (agents: look, then fix)",
                "officecli validate report.docx",
                "officecli close report.docx               # flush resident before non-officecli readers",
            },
            ["get"] = new[]
            {
                "officecli get report.docx /body/p[1] # read one paragraph",
                "officecli get slides.pptx '/slide[1]' --depth 1 --json # machine form",
            },
            ["set"] = new[]
            {
                "officecli set data.xlsx '/Sheet1/A1' --prop value=Name # write a cell",
                "officecli set report.docx '/body/p[2]' --find old --replace new # find/replace text",
            },
            ["add"] = new[]
            {
                "officecli add report.docx /body --type paragraph --prop text=\"Hi\" # append a paragraph",
                "officecli add slides.pptx /slide[1] --type shape --prop text=\"Box\" --prop x=1cm --prop y=2cm # add a shape",
            },
            ["query"] = new[]
            {
                "officecli query report.docx 'paragraph[style!=Normal]' # non-plain paragraphs",
                "officecli query data.xlsx 'cell[value>100]' --json # machine form",
            },
            ["view"] = new[]
            {
                "officecli view deck.pptx html # rendered HTML snapshot",
                "officecli view report.docx screenshot # PNG to look at before fixing",
            },
            ["create"] = new[] { "officecli create blank.xlsx # new document (docx/xlsx/pptx)" },
            ["validate"] = new[] { "officecli validate report.docx # schema check" },
            ["close"] = new[] { "officecli close report.docx # flush + stop resident" },
            ["batch"] = new[]
            {
                "officecli batch report.docx --commands '[{\"command\":\"add\",\"parent\":\"/body\",\"type\":\"paragraph\",\"props\":{\"text\":\"Hi\"}}]'",
                "officecli dump report.docx / > replay.json && officecli batch fresh.docx --input replay.json # round-trip replay",
            },
            ["help"] = new[]
            {
                "officecli help docx # list docx elements",
                "officecli help pptx add chart # drill: verb + element",
                "officecli help all | grep chart # grep the flat corpus dump",
            },
        };

    // (name, what it does, default). Single in-CLI registry for the help
    // face; the README table is the human-first projection. None of these
    // carry secrets, so no value masking applies (the set:/**** masking rule
    // targets credential-type variables).
    private static readonly (string Name, string Desc, string Default)[] EnvVars =
    {
        ("OFFICECLI_ENVELOPE", "machine envelope shape: compat (ok + success legacy) or strict (ok-only; errors as string + stderr single-line)", "compat"),
        ("OFFICECLI_LEDGER_API", "ledger base URL override for issue/artifact (tests/canary)", "https://ledger.ohmygh.com"),
        ("OFFICECLI_LEDGER_KEY", "Ed25519 seed for ledger writes (hex or base64, 32 bytes); explicit-but-invalid is an error, never a silent fallback", "(unset)"),
        ("OFFICECLI_LEDGER_KEY_FILE", "ledger private-key file (default ~/.officecli/ledger/officecli-ed25519.key); the seed never enters argv", "(keyfile)"),
        ("OFFICECLI_RESIDENT_FLUSH", "resident flush policy: each|auto|<seconds>|off", "auto"),
        ("OFFICECLI_RESIDENT_IDLE_SECONDS", "resident idle exit in seconds", "7200"),
        ("OFFICECLI_NO_AUTO_RESIDENT", "set 1 to forbid auto-starting a resident (explicit open still works)", "unset"),
        ("OFFICECLI_WATCH_ALLOWED_HOSTS", "origin hosts allowed by the watch preview server", "localhost"),
        ("OFFICECLI_SKIP_UPDATE", "set 1 to skip the non-blocking update check", "unset"),
        ("OFFICECLI_BATCH_ALLOW_STDIN_REDIRECT", "allow batch to read commands from redirected stdin (off by default: guards scripted misuse)", "unset"),
        ("OFFICECLI_LOCAL_BINARY", "installer uses this local binary (offline installs)", "unset"),
        ("OFFICECLI_MMDC", "mermaid CLI (mmdc) path override", "mmdc on PATH"),
        // OFFICECLI_ISSUES_API retired with the ledger switch (REQ-063); the
        // canary override lives in OFFICECLI_LEDGER_API now.
    };

    // ==================== root (group) face ====================

    internal static void RenderRoot(RootCommand root, TextWriter w)
    {
        HeaderLine(root, w);
        w.WriteLine(UsageLine(root));
        w.WriteLine();

        var subs = root.Subcommands.Where(c => !c.Hidden
                && !c.Name.StartsWith("__", StringComparison.Ordinal)).ToList();
        if (subs.Count > 0)
        {
            w.WriteLine("Commands:");
            WriteRows(w, subs.Select(c => (Label: c.Name, Desc: Blurb(c.Description))));
            w.WriteLine();
        }

        // Root-only flags (also the place --version lives; per-command flags
        // appear on their own leaves, never here).
        w.WriteLine("Global Options:");
        WriteRows(w, RootOptionRows(root).Select(r => (r.Label, r.Desc)));
        w.WriteLine();

        WriteExamples("root", w);

        w.WriteLine("Environment Variables:");
        WriteRows(w, EnvVars.Select(e => (Label: e.Name, Desc: $"{e.Desc} (default: {e.Default})")));
    }

    // ==================== leaf face ====================

    internal static void RenderCommand(Command cmd, RootCommand? rootForGlobals, TextWriter w)
    {
        HeaderLine(cmd, w);
        w.WriteLine(UsageLine(cmd));
        w.WriteLine();

        var args = cmd.Arguments.Where(a => !a.Hidden).ToList();
        if (args.Count > 0)
        {
            w.WriteLine("Arguments:");
            WriteRows(w, args.Select(a =>
            {
                var label = ArgLabel(a);
                var desc = a.Description ?? "";
                if (DefaultValue(a) is { } dv)
                    desc += $" (default: {dv})";
                return (label, desc);
            }));
            w.WriteLine();
        }

        var common = rootForGlobals != null ? DeriveCommonFlags(rootForGlobals) : [];
        var own = cmd.Options.Where(o => !o.Hidden && !common.Contains(o.Name)).ToList();
        if (own.Count > 0)
        {
            w.WriteLine("Options:");
            WriteRows(w, own.Select(OptionRow));
            w.WriteLine();
        }

        WriteExamples(cmd.Name, w);

        var globals = cmd.Options.Where(o => !o.Hidden && common.Contains(o.Name))
            .Select(OptionRow).ToList();
        globals.Add(("--help, -h, -?", "Show help and usage information"));
        if (globals.Count > 0)
        {
            w.WriteLine("Global Options:");
            WriteRows(w, globals.Select(r => (r.Label, r.Desc)).OrderBy(r => r.Label, StringComparer.Ordinal));
        }
    }

    // ==================== pieces ====================

    private static void HeaderLine(Command cmd, TextWriter w)
    {
        var blurb = Blurb(cmd is RootCommand ? FirstLine(cmd.Description).Replace("officecli: ", "") : cmd.Description, cap: 110);
        w.WriteLine($"officecli@{Version} {blurb}");
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var nl = text.IndexOf('\n');
        return nl > 0 ? text[..nl].Trim() : text.Trim();
    }

    private static string UsageLine(Command cmd)
    {
        var parts = new List<string> { "officecli" };
        if (cmd is not RootCommand) parts.Add(cmd.Name);
        foreach (var a in cmd.Arguments.Where(a => !a.Hidden))
            parts.Add(ArgLabel(a));
        if (cmd is RootCommand) parts.Add("[command]");
        parts.Add("[options]");
        return "Usage: " + string.Join(" ", parts);
    }

    private static string ArgLabel(Argument a)
    {
        var token = $"<{a.Name}>";
        if (a.Arity.MaximumNumberOfValues > 1) token = $"<{a.Name}...>";
        if (a.Arity.MinimumNumberOfValues == 0) token = $"[{token}]";
        return token;
    }

    private static IEnumerable<(string Label, string Desc)> RootOptionRows(RootCommand root)
    {
        // Synthetic help row (the built-in help option lives on the root's
        // option list with an SCL-internal description), plus the Program-level
        // family flags (issue #14 wave a) dispatched before the tree — same
        // registry SchemaFace serves, one source.
        yield return ("--help, -h, -?", "Show help and usage information");
        foreach (var row in root.Options.Where(o => !o.Hidden && o.Name != "--help")
                     .Select(OptionRow)
                     .Concat(SchemaFace.FamilyFlags.Select(f => (f.Name, f.Desc)))
                     .OrderBy(r => r.Item1, StringComparer.Ordinal))
            yield return row;
    }

    private static (string Label, string Desc) OptionRow(Option o)
    {
        // Long flag first, then short aliases (standard: `--kebab, -s <type>`).
        var names = o.Aliases.Where(a => a != null).Select(a => a!)
            .OrderByDescending(a => a.Length).ThenBy(a => a, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0) names.Add(o.Name ?? "--?");
        var label = string.Join(", ", names);
        var placeholder = ValuePlaceholder(o);
        if (placeholder != null) label += $" {placeholder}";
        var desc = o.Description ?? "";
        if (desc.Length > 0 && DefaultValue(o) is { } dv && dv.ToString() != "False")
            desc += $" (default: {dv})"; // options default-suffix
        return (label, desc);
    }

    private static string? ValuePlaceholder(Option o)
    {
        var t = Nullable.GetUnderlyingType(o.ValueType) ?? o.ValueType;
        if (t == typeof(bool) || t == typeof(void)) return null;
        string placeholder = t.IsEnum
            ? string.Join("|", Enum.GetNames(t).Select(n => n.ToLowerInvariant()))
            : t == typeof(string) ? "value"
            : t == typeof(string[]) ? "value"
            : t == typeof(int) || t == typeof(long) || t == typeof(double) ? "n"
            : t == typeof(System.IO.FileInfo) ? "path"
            : t.Name.ToLowerInvariant();
        var wrapped = $"<{placeholder}>";
        return o.Arity.MaximumNumberOfValues > 1 ? wrapped + "..." : wrapped;
    }

    /// <summary>Declared default via the non-generic base API (trim-safe: no
    /// reflection). Null when no default factory was set.</summary>
    private static object? DefaultValue(Option o) => o.HasDefaultValue ? o.GetDefaultValue() : null;

    private static object? DefaultValue(Argument a) => a.HasDefaultValue ? a.GetDefaultValue() : null;

    private static void WriteExamples(string key, TextWriter w)
    {
        if (!Examples.TryGetValue(key, out var lines) || lines.Length == 0) return;
        // Align the `# comment` tails like the manual does.
        var split = lines.Select(SplitComment).ToList();
        var pad = split.Where(s => s.Comment != null)
            .Select(s => s.Cmd.Length).DefaultIfEmpty(0).Max() + 2;
        w.WriteLine("Examples:");
        foreach (var (cmdText, comment) in split)
            w.WriteLine(comment == null ? $"  {cmdText}"
                : $"  {cmdText.PadRight(pad)}# {comment}");
        w.WriteLine();
    }

    private static (string Cmd, string? Comment) SplitComment(string line)
    {
        var idx = line.IndexOf(" # ", StringComparison.Ordinal);
        return idx < 0 ? (line, null) : (line[..idx].TrimEnd(), line[(idx + 3)..]);
    }

    private static void WriteRows(TextWriter w, IEnumerable<(string Label, string Desc)> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var pad = Math.Max(list.Max(r => r.Label.Length) + 2, 4);
        var width = Math.Max(TotalWidth - pad, 40);
        foreach (var (label, desc) in list)
        {
            var first = true;
            foreach (var seg in Wrap(desc, width))
            {
                w.WriteLine(first ? label.PadRight(pad) + seg : new string(' ', pad) + seg);
                first = false;
            }
            if (first) w.WriteLine(label); // empty description
        }
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length == 0) line.Append(word);
            else if (line.Length + 1 + word.Length <= width) line.Append(' ').Append(word);
            else
            {
                yield return line.ToString();
                line.Clear().Append(word);
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
