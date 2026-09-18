// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OfficeCli.Core;

/// <summary>
/// `officecli --llms` — agent-facing compact manual rendered from the LIVE
/// command tree (REQ-060 face 1; flag name is the family standard per
/// 总台更正单 2026-09-17, omc D31 form): never hand-maintained, cannot drift
/// from the real surface. Bare `--llms` prints a markdown manual (name,
/// version, one-line positioning, subcommand table, common flags, runnable
/// examples, ≤120 lines); `--llms --json` prints the machine form.
/// </summary>
internal static class LlmsManual
{
    private const int MaxLines = 120;

    public static int Run(bool json)
    {
        var root = CommandBuilder.BuildRootCommand();
        var version = typeof(LlmsManual).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        // Live subcommand list: internal (__...) commands stay hidden.
        var commands = root.Subcommands
            .Where(c => !c.Name.StartsWith("__", StringComparison.Ordinal))
            .Select(c => (Name: c.Name, Desc: OfficeCli.Help.HelpFace.Blurb(c.Description),
                          Flags: c.Options.Select(o => o.Name).Where(n => n is not null).ToList()))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        // Common flags = shared by >= 4 subcommands (live-derived, not curated);
        // same derivation drives the help face's Global Options split.
        var commonFlags = OfficeCli.Help.HelpFace.DeriveCommonFlags(root);

        if (json)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("name", "officecli");
                writer.WriteString("version", version);
                writer.WriteString("description",
                    "AI-friendly CLI for Office documents (.docx/.xlsx/.pptx): single self-contained binary, no Office install; renders documents to HTML/PNG so agents can look at what they edit.");
                writer.WriteStartArray("globalFlags");
                foreach (var f in commonFlags) writer.WriteStringValue(f);
                writer.WriteEndArray();
                writer.WriteStartArray("commands");
                foreach (var c in commands)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", c.Name);
                    writer.WriteString("description", c.Desc);
                    writer.WriteStartArray("flags");
                    foreach (var f in c.Flags.Where(f => !commonFlags.Contains(f)))
                        writer.WriteStringValue(f);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
            return 0;
        }

        var lines = new List<string>
        {
            "# officecli",
            "",
            $"Version: {version}",
            "",
            "AI-friendly CLI for Office documents (.docx/.xlsx/.pptx): single self-contained binary,",
            "no Office install; renders documents to HTML/PNG so agents can look at what they edit.",
            "",
            "## Read order",
            "",
            "Commands below — every verb takes the file path first (`officecli <verb> <file> ...`).",
            "Machine form of this manual: `officecli --llms --json`. Per-format element reference:",
            "`officecli help <format> [<verb>] [<element>]`. Exit codes at the bottom.",
            "",
            "## Subcommands",
            "",
            "| command | what it does |",
            "|---|---|",
        };
        foreach (var c in commands)
            lines.Add($"| `{c.Name}` | {c.Desc} |");

        lines.Add("");
        lines.Add("## Common flags");
        lines.Add("");
        lines.Add(string.Join(" ", commonFlags.Select(f => $"`{f}`")));
        lines.Add("");
        lines.Add("## Exit codes");
        lines.Add("");
        lines.Add("| code | meaning |");
        lines.Add("|---|---|");
        lines.Add("| 0 | success |");
        lines.Add("| 1 | business miss (file not found, no match, already exists) |");
        lines.Add("| 2 | usage or system error (unknown command, bad flag, missing argument) |");
        lines.Add("");
        lines.Add("## Output contract");
        lines.Add("");
        lines.Add("`--json` wraps output in an envelope `{ok, success, data | error, warnings?}` (`ok` mirrors");
        lines.Add("`success`); the error object carries `code` + `suggestion` + typed `meta.cta`. Family flags:");
        lines.Add("`--format toon|json|yaml|md`, `--filter-output <keys>`, `--full-output`, `<cmd> --schema`.");
        lines.Add("Strict preview: OFFICECLI_ENVELOPE=strict drops success/message and puts errors");
        lines.Add("on stderr as one-line {code, message, cta}. Field order stable (never alphabetized).");
        lines.Add("");
        lines.Add("## Runnable examples");
        lines.Add("");
        lines.Add("```bash");
        lines.AddRange(OfficeCli.Help.HelpFace.Examples["root"]);
        lines.Add("```");
        lines.Add("");
        lines.Add("Hit a defect? File it one-key: `officecli issue new \"<title>\" --body \"<repro>\"`.");

        if (lines.Count > MaxLines)
        {
            // Hard budget: subcommand table grows with the tree — fail loudly rather
            // than ship a manual agents won't finish reading.
            Console.Error.WriteLine($"--llms manual is {lines.Count} lines (budget {MaxLines}); trim the table or examples.");
            return 1;
        }
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return 0;
    }
}
