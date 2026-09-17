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
            .Select(c => (Name: c.Name, Desc: FirstSentence(c.Description),
                          Flags: c.Options.Select(o => o.Name).Where(n => n is not null).ToList()))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        // Common flags = shared by >= 4 subcommands (live-derived, not curated).
        var commonFlags = commands
            .SelectMany(c => c.Flags.Distinct())
            .GroupBy(f => f, StringComparer.Ordinal)
            .Where(g => g.Count() >= 4)
            .Select(g => g.Key!)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

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
            "Generated from the live command tree — the agent-facing source of truth (`officecli --llms --json`",
            "for the machine form; `officecli help` for schema-driven detail per format/element).",
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
        lines.Add("## Runnable examples");
        lines.Add("");
        lines.Add("```bash");
        lines.Add("officecli create report.docx");
        lines.Add("officecli add report.docx /body --type paragraph --prop text=\"Summary\" --prop style=Heading1");
        lines.Add("officecli set data.xlsx '/Sheet1/A1' --prop value=Name --prop bold=true");
        lines.Add("officecli get slides.pptx '/slide[1]' --depth 1 --json");
        lines.Add("officecli query report.docx 'paragraph[style!=Normal]'");
        lines.Add("officecli view deck.pptx html            # rendered snapshot (agents: look, then fix)");
        lines.Add("officecli validate report.docx");
        lines.Add("officecli close report.docx               # flush resident before non-officecli readers");
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

    private static string FirstSentence(string? description)
    {
        if (string.IsNullOrEmpty(description)) return "";
        var text = description.Trim();
        var cut = text.IndexOfAny(['.', ':', ';', '\n']);
        if (cut > 0) text = text[..cut];
        return text.Length <= 90 ? text : text[..90] + "…";
    }
}
