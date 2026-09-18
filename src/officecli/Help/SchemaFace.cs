// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Reflection;
using System.Text.Json.Nodes;
using OfficeCli.Core;

namespace OfficeCli.Help;

/// <summary>
/// `officecli [&lt;cmd&gt;] --schema` — cli-docs 输出协议旗标族件(issue #14 wave a):
/// per-command JSON Schema rendered from the LIVE command tree (args, options,
/// output contract), same single source as the help face and --llms manual.
/// Dispatched at Program level (canonical position: right after the command
/// name), emitted envelope-wrapped so it shares the {ok, success, data} machine
/// face with every other command.
/// </summary>
internal static class SchemaFace
{
    private static string Version => (typeof(SchemaFace).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    public static int Run(string? commandName)
    {
        var root = CommandBuilder.BuildRootCommand();
        Command cmd = root;
        if (commandName != null)
        {
            cmd = root.Subcommands.FirstOrDefault(c =>
                       string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase) && !c.Hidden)
                   ?? root;
        }

        // Root description begins "officecli: …" — strip the self-naming prefix
        // BEFORE the first-sentence blurb (Blurb would otherwise cut at that
        // colon and leave just the name).
        var rawDescription = cmd.Description ?? "";
        if (rawDescription.StartsWith("officecli: ", StringComparison.Ordinal))
            rawDescription = rawDescription["officecli: ".Length..];
        var description = HelpFace.Blurb(rawDescription, cap: 300);

        var schema = new JsonObject
        {
            ["name"] = "officecli",
            ["version"] = Version,
            ["command"] = cmd is RootCommand ? "" : cmd.Name,
            ["description"] = description,
        };

        var arguments = new JsonArray();
        foreach (var a in cmd.Arguments.Where(a => !a.Hidden))
        {
            var arg = new JsonObject
            {
                ["name"] = a.Name,
                ["required"] = a.Arity.MinimumNumberOfValues > 0,
                ["variadic"] = a.Arity.MaximumNumberOfValues > 1,
                ["type"] = TypeName(a.ValueType),
                ["description"] = a.Description ?? "",
            };
            if (a.HasDefaultValue && a.GetDefaultValue() is { } dv) arg["default"] = dv.ToString();
            arguments.Add(arg);
        }
        schema["arguments"] = arguments;

        var options = new JsonArray();
        foreach (var o in cmd.Options.Where(o => !o.Hidden))
        {
            var opt = new JsonObject
            {
                ["name"] = o.Name,
                ["aliases"] = JsonArray(o.Aliases.Where(x => x != o.Name)),
                ["type"] = TypeName(o.ValueType),
                ["required"] = o.Required,
                ["description"] = o.Description ?? "",
            };
            if (o.ValueType.IsEnum)
                opt["enum"] = JsonArray(Enum.GetNames(o.ValueType).Select(n => n.ToLowerInvariant()));
            if (o.HasDefaultValue && o.GetDefaultValue() is { } dv && dv.ToString() != "False")
                opt["default"] = dv.ToString();
            options.Add(opt);
        }
        // Program-level family flags (dispatched before the tree — see Program.cs)
        foreach (var (name, desc) in FamilyFlags)
            options.Add(new JsonObject { ["name"] = name, ["aliases"] = new JsonArray(), ["type"] = TypeName(typeof(void)), ["required"] = false, ["description"] = desc });
        schema["options"] = options;

        schema["output"] = new JsonObject
        {
            ["envelope"] = "{ok, success, data | error, warnings?, meta?} (ok mirrors success; error carries code + suggestion)",
            ["formats"] = "toon (default) | json | yaml | md via --format",
            ["exit_codes"] = new JsonObject { ["0"] = "success", ["1"] = "business miss", ["2"] = "usage or system error" },
        };

        Console.WriteLine(OutputFormatter.WrapEnvelope(schema.ToJsonString()));
        return 0;
    }

    /// <summary>The Program-level flags every command accepts but the tree does
    /// not know about (dispatch happens before parsing). Same list as the root
    /// help face's synthetic Global Options rows.</summary>
    internal static readonly (string Name, string Desc)[] FamilyFlags =
    {
        ("--llms", "agent-facing compact manual (markdown; --llms --json machine form)"),
        ("--schema", "this command's JSON Schema (args, options, output contract)"),
        ("--format <toon|json|yaml|md>", "output format family (non-toon implies machine envelope)"),
        ("--filter-output <keys>", "key-path filter, envelope-rooted (data.path.to.key,data.arr[0].field)"),
        ("--full-output", "full envelope incl. meta {command, duration_ms, cta}"),
    };

    private static JsonArray JsonArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        // Cast to JsonNode (not the generic Add<T>): under PublishTrimmed the
        // generic overload pulls reflection-based metadata that is trimmed away.
        foreach (var i in items) arr.Add((JsonNode?)i);
        return arr;
    }

    private static string TypeName(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u == typeof(bool) ? "boolean"
            : u == typeof(string) ? "string"
            : u == typeof(string[]) ? "string[]"
            : u == typeof(int) || u == typeof(long) || u == typeof(short) ? "integer"
            : u == typeof(double) || u == typeof(float) || u == typeof(decimal) ? "number"
            : u == typeof(System.IO.FileInfo) ? "path"
            : u == typeof(void) ? "flag"
            : u.IsEnum ? "enum"
            : u.Name.ToLowerInvariant();
    }
}
