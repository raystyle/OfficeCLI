// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OfficeCli.Core;

/// <summary>
/// Unified issue entry (issues.ohmygh.com, REQ-057 contract): agents hit a
/// defect mid-session and file it one-key with automatic context — tool,
/// version, platform, host. Read face lists/shows per tool.
///   officecli issue new "<title>" [--body <text>] [--tool <name>]
///   officecli issue list [--tool <t>] [--status <s>] [--limit <n>] [--json]
///   officecli issue show <id> [--json]
/// Contract mirrors the omc reference implementation exactly: reject on
/// tool-shape / title-length / body-length errors; client-side trim+clip for
/// version(40) / platform(64) / host(64). Base URL overridable via
/// OFFICECLI_ISSUES_API for tests and canary routing.
/// </summary>
internal static class IssueCli
{
    private const string DefaultApiBase = "https://issues.ohmygh.com";
    private const int TitleMax = 200;
    private const int BodyMax = 20000;

    private static string ApiBase =>
        Environment.GetEnvironmentVariable("OFFICECLI_ISSUES_API") ?? DefaultApiBase;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            WriteUsage(Console.Out);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            switch (args[0])
            {
                case "new":
                    return RunNew(args);
                case "list":
                    return RunList(args);
                case "show":
                    return RunShow(args);
                default:
                    Console.Error.WriteLine($"Unknown issue subcommand: {args[0]}");
                    WriteUsage(Console.Error);
                    return 1;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Network error talking to {ApiBase}: {ex.Message}");
            Console.Error.WriteLine("Check connectivity, or set OFFICECLI_ISSUES_API to override the base URL.");
            return 1;
        }
        catch (TaskCanceledException) // HttpClient timeout
        {
            Console.Error.WriteLine($"Timed out talking to {ApiBase}.");
            return 1;
        }
    }

    // ---- new ---------------------------------------------------------------
    private static int RunNew(string[] args)
    {
        string? title = null, body = null, tool = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--title":
                    if (++i >= args.Length) return BadFlag("--title needs a value");
                    title = args[i];
                    break;
                case "--body":
                    if (++i >= args.Length) return BadFlag("--body needs a value");
                    body = args[i];
                    break;
                case "--tool":
                    if (++i >= args.Length) return BadFlag("--tool needs a value");
                    tool = args[i];
                    break;
                default:
                    if (title is null && !args[i].StartsWith('-')) title = args[i];
                    else return BadFlag($"unexpected argument: {args[i]}");
                    break;
            }
        }
        if (title is null)
        {
            Console.Error.WriteLine("issue new needs a title (positional or --title).");
            return 1;
        }

        tool ??= "officecli";
        var payload = Validate(tool, title, body ?? "");
        if (payload is null) return 1;

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = NewClient(handler);
        using var content = new StringContent(payload.Value.json, Encoding.UTF8, "application/json");
        using var response = client.PostAsync($"{ApiBase}/api/issues", content).GetAwaiter().GetResult();
        var respText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if ((int)response.StatusCode == 429)
        {
            Console.Error.WriteLine("Rate limited (10 issues/hour per IP). Try again later.");
            return 1;
        }
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Server rejected the issue (HTTP {(int)response.StatusCode}): {ExtractError(respText)}");
            return 1;
        }
        using var doc = JsonDocument.Parse(respText);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("id", out var id) || !root.TryGetProperty("url", out var url))
        {
            Console.Error.WriteLine($"Unrecognized server response: {respText}");
            return 1;
        }
        Console.WriteLine($"Filed issue #{id.GetInt32()} for {payload.Value.tool}");
        Console.WriteLine($"  {url.GetString()}");
        Console.WriteLine("  (list: officecli issue list)");
        return 0;
    }

    // ---- list --------------------------------------------------------------
    private static int RunList(string[] args)
    {
        string? tool = null, status = null;
        int limit = 20;
        bool json = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tool": tool = Value(args, ref i, "--tool"); break;
                case "--status": status = Value(args, ref i, "--status"); break;
                case "--json": json = true; break;
                case "--limit":
                    var raw = Value(args, ref i, "--limit");
                    if (!int.TryParse(raw, out limit) || limit < 1 || limit > 100)
                    {
                        Console.Error.WriteLine("--limit must be an integer 1..100.");
                        return 1;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"unexpected argument: {args[i]}");
                    return 1;
            }
        }

        var query = new List<string> { $"tool={Uri.EscapeDataString(tool ?? "officecli")}" };
        if (!string.IsNullOrEmpty(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        query.Add($"limit={limit}");

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = NewClient(handler);
        var respText = client.GetStringAsync($"{ApiBase}/api/issues?{string.Join("&", query)}").GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(respText);
        if (json)
        {
            Console.WriteLine(doc.RootElement.GetRawText());
            return 0;
        }
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out var err))
        {
            Console.Error.WriteLine($"Server error: {err.GetString()}");
            return 1;
        }
        // Response envelope: {ok:true, count:N, issues:[...]} (newest first).
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("issues", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            Console.Error.WriteLine($"Unrecognized list response: {respText}");
            return 1;
        }
        foreach (var row in rows.EnumerateArray())
        {
            var id = GetStr(row, "id").PadLeft(5);
            var t = GetStr(row, "tool").PadRight(10);
            var st = GetStr(row, "status").PadRight(7);
            var created = GetStr(row, "created_at");
            if (created.Length > 16) created = created[..16];
            var title = GetStr(row, "title");
            if (title.Length > 60) title = title[..60];
            Console.WriteLine($"#{id}  {t}  {st}  {created}  {title}");
        }
        return 0;
    }

    // ---- show --------------------------------------------------------------
    private static int RunShow(string[] args)
    {
        string? id = null;
        bool json = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (id is null && !args[i].StartsWith('-')) { id = args[i]; continue; }
            Console.Error.WriteLine($"unexpected argument: {args[i]}");
            return 1;
        }
        if (id is null)
        {
            Console.Error.WriteLine("issue show needs an issue id.");
            return 1;
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = NewClient(handler);
        var respText = client.GetStringAsync($"{ApiBase}/api/issues/{Uri.EscapeDataString(id)}").GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(respText);
        if (json)
        {
            Console.WriteLine(doc.RootElement.GetRawText());
            return 0;
        }
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
        {
            Console.Error.WriteLine($"Server error: {err.GetString()}");
            return 1;
        }
        // Response envelope: {ok:true, issue:{...}}.
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("issue", out var issueEl)
            || issueEl.ValueKind != JsonValueKind.Object)
        {
            Console.Error.WriteLine($"Unrecognized show response: {respText}");
            return 1;
        }
        root = issueEl;
        Console.WriteLine($"Issue #{GetStr(root, "id")} [{GetStr(root, "status")}] {GetStr(root, "title")}");
        Console.WriteLine($"  tool:     {GetStr(root, "tool")}");
        Console.WriteLine($"  version:  {GetStr(root, "version")}");
        Console.WriteLine($"  platform: {GetStr(root, "platform")}");
        Console.WriteLine($"  host:     {GetStr(root, "host")}");
        Console.WriteLine($"  created:  {GetStr(root, "created_at")}");
        Console.WriteLine();
        Console.WriteLine(GetStr(root, "body"));
        return 0;
    }

    // ---- shared bits -------------------------------------------------------
    private static (string tool, string json)? Validate(string tool, string title, string body)
    {
        tool = tool.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(tool, "^[a-z][a-z0-9_-]{0,31}$"))
        {
            Console.Error.WriteLine("tool must match ^[a-z][a-z0-9_-]{0,31}$");
            return null;
        }
        title = title.Trim();
        if (title.Length < 1) { Console.Error.WriteLine("title cannot be empty."); return null; }
        if (title.Length > TitleMax) { Console.Error.WriteLine($"title too long (max {TitleMax})."); return null; }
        if (body.Length > BodyMax) { Console.Error.WriteLine($"body too long (max {BodyMax})."); return null; }

        // Client-side context: auto version/platform/host, trimmed+clipped per contract.
        var version = Clip(SelfVersion(), 40);
        var platform = Clip(SelfPlatform(), 64);
        var host = Clip(Environment.MachineName, 64);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", tool);
            writer.WriteString("title", title);
            writer.WriteString("body", body);
            writer.WriteString("version", version);
            writer.WriteString("platform", platform);
            writer.WriteString("host", host);
            writer.WriteEndObject();
        }
        return (tool, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    internal static string SelfVersion() =>
        typeof(IssueCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    internal static string SelfPlatform()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
               : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
               : RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return $"{os}/{arch}";
    }

    private static string Clip(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static HttpClient NewClient(HttpClientHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"officecli/{SelfVersion()} issue");
        return client;
    }

    private static string GetStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? "")
            : "";

    private static string? ExtractError(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            return doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : respText;
        }
        catch (JsonException) { return respText; }
    }

    private static string Value(string[] args, ref int i, string flag)
    {
        if (++i >= args.Length) throw new ArgumentException($"{flag} needs a value");
        return args[i];
    }

    private static int BadFlag(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  officecli issue new \"<title>\" [--body <text>] [--tool <name>]");
        writer.WriteLine("      File an issue (auto context: tool=officecli, version, platform, host)");
        writer.WriteLine("  officecli issue list [--tool <t>] [--status <s>] [--limit <n>] [--json]");
        writer.WriteLine("      List issues, newest first (default tool=officecli, limit=20)");
        writer.WriteLine("  officecli issue show <id> [--json]");
        writer.WriteLine("      Show one issue in full");
        writer.WriteLine();
        writer.WriteLine("Unified fleet issue tracker: https://issues.ohmygh.com (REQ-057).");
        writer.WriteLine("Agents: hit a defect mid-session — file it one-key, no context gathering needed.");
    }
}
