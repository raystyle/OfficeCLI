// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OfficeCli.Core.Ledger;

namespace OfficeCli.Core;

/// <summary>
/// `officecli issue` and `officecli artifact` — the REQ-063 仓级公共账本
/// command family (ledger.ohmygh.com, the new source of truth; the old
/// issues.ohmygh.com face is retired from this CLI). Issues track obligation
/// (bug / improvement with acceptance); artifacts are the shared library
/// (experience / lesson / research / … registered by content digest). Writes
/// are Ed25519-signed by <see cref="LedgerClient"/>; reads follow the family
/// pagination form (limit 100 + before cursor + has_more saturation hint).
/// </summary>
internal static class LedgerCli
{
    internal static string SelfVersion() =>
        typeof(LedgerCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "";

    // ==================== issue ====================

    public static int RunIssue(string[] args) => Run(args, issue: true);

    public static int RunArtifact(string[] args) => Run(args, issue: false);

    private static int Run(string[] args, bool issue)
    {
        var rest = args.Length > 0 ? args[1..] : [];
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        try
        {
            if (issue)
            {
                switch (cmd)
                {
                    case "": case "help": UsageIssue(); return 0;
                    case "new": return IssueNew(rest);
                    case "list": return IssueList(rest);
                    case "show": return IssueShow(rest);
                }
            }
            else
            {
                switch (cmd)
                {
                    case "": case "help": UsageArtifact(); return 0;
                    case "publish": return ArtifactPublish(rest);
                    case "attest": return ArtifactAttest(rest);
                    case "list": return ArtifactList(rest);
                }
            }
        }
        catch (InvalidOperationException ex) // bad input / missing key
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2; // usage-class per the 0/1/2 contract (G11)
        }
        catch (HttpRequestException ex) // ledger unreachable (F1: old face
        // caught these; the new one crashed rc 134)
        {
            Console.Error.WriteLine($"ledger unreachable: {ex.Message}");
            Console.Error.WriteLine($"(check connectivity; base is {LedgerClient.ApiBase}, override via OFFICECLI_LEDGER_API)");
            return 2; // system error
        }
        catch (TaskCanceledException) // HttpClient timeout — same class as unreachable
        {
            Console.Error.WriteLine($"ledger timed out (base {LedgerClient.ApiBase}).");
            return 2;
        }
        catch (JsonException ex) // unparseable server response
        {
            Console.Error.WriteLine($"ledger response not valid JSON: {ex.Message}");
            return 2;
        }
        if (issue) UsageIssue(); else UsageArtifact();
        Console.Error.WriteLine($"error: unknown subcommand '{cmd}'.");
        return 2;
    }

    private static int IssueNew(string[] a)
    {
        string? kind = null, acceptance = null;
        var pos = ParseArgs(a,
            ("--kind", v => kind = v), ("--acceptance", v => acceptance = v),
            ("--body", v => acceptance = acceptance == null ? v : acceptance + "\n" + v),
            ("--json", null));
        var title = pos.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("issue new needs a title: officecli issue new \"<title>\" --kind bug|improvement --acceptance \"<criteria>\"");
        kind ??= "bug";
        if (!LedgerClient.IssueKinds.Contains(kind))
            throw new InvalidOperationException($"unknown issue kind '{kind}'. Valid: bug, improvement.");
        if (string.IsNullOrWhiteSpace(acceptance))
            throw new InvalidOperationException("issue new needs --acceptance \"<criteria>\" (closures run through the omc workbench, not this CLI).");

        var body = new JsonObject { ["title"] = title, ["kind"] = kind, ["acceptance"] = acceptance };
        var (status, resp) = LedgerClient.PostAsync(Path("issues"), body).GetAwaiter().GetResult();
        return Emit(status, resp, $"Issue opened on {LedgerClient.ApiBase} (repo {LedgerClient.RepoId}).", json: a.Contains("--json"));
    }

    private static int IssueList(string[] a)
    {
        int limit = 100;
        string? before = null;
        bool json = a.Contains("--json");
        ParseArgs(a,
            ("--limit", v => { if (!int.TryParse(v, out limit) || limit is < 1 or > 100) throw new InvalidOperationException("--limit must be 1..100 (family form caps a page at 100)."); }),
            ("--before", v => before = v),
            ("--json", null));
        var query = $"?limit={limit}&more=1" + (before != null ? $"&before={Uri.EscapeDataString(before)}" : "");
        var (status, resp) = LedgerClient.GetAsync(Path("issues") + query).GetAwaiter().GetResult();
        if (json || status != 200) return Emit(status, resp, null, json);

        var root = JsonNode.Parse(resp);
        var items = Items(root, "issues");
        Console.WriteLine($"Issues for {LedgerClient.RepoId} (count {items.Count}{(HasMore(root) ? ", more available" : ", saturated")}):");
        foreach (var item in items)
        {
            var n = Field(item, "issue_n", "n", "number", "id", "seq");
            var title = Field(item, "title") ?? "";
            var kind = Field(item, "kind") ?? "bug";
            var st = Field(item, "status") ?? "open";
            Console.WriteLine($"  #{n,-4} [{st,-7}] {kind,-12} {title}");
        }
        if (HasMore(root) && items.Count > 0)
        {
            var last = Field(items[^1], "issue_n", "n", "number", "id", "seq");
            if (!string.IsNullOrEmpty(last))
                Console.WriteLine($"more: officecli issue list --before {last}");
        }
        return 0;
    }

    private static int IssueShow(string[] a)
    {
        var pos = ParseArgs(a, ("--json", null));
        var n = pos.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(n) || !int.TryParse(n, out _))
            throw new InvalidOperationException("issue show needs an issue number: officecli issue show <n>");
        bool json = a.Contains("--json");
        var (status, resp) = LedgerClient.GetAsync(Path($"issues/{n}")).GetAwaiter().GetResult();
        if (json) return Emit(status, resp, null, json);
        if (status != 200) return Emit(status, resp, null);

        // Detail face: {issue, projection:{status,kind,...}, timeline:[events]}.
        // Title/acceptance live inside the issue_open event's payload string.
        var root = JsonNode.Parse(resp) as JsonObject ?? [];
        var projection = root["projection"] as JsonObject ?? [];
        string? title = null, acceptance = null;
        if (root["timeline"] is JsonArray timeline)
            foreach (var e in timeline.OfType<JsonObject>())
            {
                if (Field(e, "type") != "issue_open" || e["payload"] is not JsonValue payload) continue;
                try
                {
                    if (JsonNode.Parse(payload.ToString()) is JsonObject open)
                    {
                        title ??= Field(open, "title");
                        acceptance ??= Field(open, "acceptance");
                    }
                }
                catch { /* malformed payload string — leave as-is */ }
            }
        Console.WriteLine($"issue #{root["issue"]} [{Field(projection, "status") ?? "open"}] {Field(projection, "kind") ?? "bug"}: {title ?? "(no title)"}");
        if (acceptance != null) Console.WriteLine($"acceptance: {acceptance}");
        if (root["timeline"] is JsonArray events)
            foreach (var e in events.OfType<JsonObject>())
            {
                var ts = Field(e, "created_at");
                var when = ts != null && long.TryParse(ts, out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm")
                    : ts ?? "";
                var line = "";
                try
                {
                    if (e["payload"] is JsonValue p && JsonNode.Parse(p.ToString()) is JsonObject po)
                        line = Field(po, "digest") ?? Field(po, "status") ?? Field(po, "note") ?? "";
                }
                catch { /* keep the bare event line */ }
                Console.WriteLine($"  {when,-16} seq {Field(e, "seq") ?? "",-4} {Field(e, "type") ?? "",-12} {line}");
            }
        Console.WriteLine($"ledger: {LedgerClient.ApiBase}/repos/{LedgerClient.RepoId}/issues/{n}");
        return 0;
    }

    // ==================== artifact ====================

    private static int ArtifactPublish(string[] a)
    {
        string? name = null, kind = null, digest = null, version = null, range = null, outcome = null, note = null;
        List<string>? deps = null;
        ParseArgs(a,
            ("--name", v => name = v),
            ("--kind", v => kind = v),
            ("--digest", v => digest = v),
            ("--version", v => version = v),
            ("--git-range", v => range = v),
            ("--git_range", v => range = v),
            ("--outcome", v => outcome = v),
            ("--note", v => note = v),
            ("--deps", v => deps = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()),
            ("--json", null));
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind) || !LedgerClient.IsValidDigest(digest))
            throw new InvalidOperationException(
                "artifact publish needs --name, --kind, --digest sha256:<64hex> (hash of the content/record — the ledger stores metadata, not bytes).");
        if (!LedgerClient.ArtifactKinds.Contains(kind))
            throw new InvalidOperationException($"unknown artifact kind '{kind}'. Valid: {string.Join(", ", LedgerClient.ArtifactKinds.OrderBy(k => k, StringComparer.Ordinal))}.");
        if (kind!.Equals("experience", StringComparison.OrdinalIgnoreCase)
            && outcome is not null and not ("success" or "failure"))
            throw new InvalidOperationException("--outcome for kind=experience must be success or failure.");

        var body = new JsonObject { ["name"] = name, ["kind"] = kind, ["digest"] = digest };
        if (version != null) body["version"] = version;
        if (range != null) body["git_range"] = range;
        if (outcome != null) body["outcome"] = outcome;
        if (note != null) body["note"] = note;
        if (deps is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var d in deps) arr.Add((JsonNode?)d);
            body["deps"] = arr;
        }
        var (status, resp) = LedgerClient.PostAsync(Path("artifacts"), body).GetAwaiter().GetResult();
        return Emit(status, resp, "Artifact registered in the shared library.", json: a.Contains("--json"));
    }

    private static int ArtifactAttest(string[] a)
    {
        string? type = null, note = null;
        var pos = ParseArgs(a, ("--type", v => type = v), ("--note", v => note = v), ("--json", null));
        var id = pos.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("artifact attest needs an artifact id.");
        if (type == null)
            throw new InvalidOperationException(
                "artifact attest needs --type attest_dev|attest_prod|verification_failed.");
        if (!LedgerClient.AttestationTypes.Contains(type))
            throw new InvalidOperationException($"unknown attestation type '{type}'. Valid: {string.Join(", ", LedgerClient.AttestationTypes.OrderBy(t => t, StringComparer.Ordinal))}.");

        var body = new JsonObject { ["type"] = type };
        if (note != null) body["note"] = note;
        var (status, resp) = LedgerClient.PostAsync(Path($"artifacts/{id}/attestations"), body).GetAwaiter().GetResult();
        return Emit(status, resp, $"Attestation '{type}' recorded for artifact {id}.", json: a.Contains("--json"));
    }

    private static int ArtifactList(string[] a)
    {
        bool current = a.Contains("--current");
        string? env = null;
        bool json = a.Contains("--json");
        ParseArgs(a, ("--env", v => env = v), ("--json", null), ("--current", null));
        if (env is not null and not ("dev" or "prod"))
            throw new InvalidOperationException("--env must be dev or prod.");
        var query = "?" + (current ? "current=1" : "current=0") + (env != null ? $"&env={env}" : "");
        var (status, resp) = LedgerClient.GetAsync(Path("artifacts") + query).GetAwaiter().GetResult();
        if (json || status != 200) return Emit(status, resp, null, json);

        var root = JsonNode.Parse(resp);
        var items = Items(root, "artifacts");
        Console.WriteLine($"Artifacts for {LedgerClient.RepoId} (count {items.Count}):");
        foreach (var item in items)
        {
            var id = Field(item, "artifact_id", "id", "n", "seq");
            Console.WriteLine($"  #{id,-38} [{Field(item, "kind") ?? "",-12}] {Field(item, "name")}");
            var envs = item["envs"] ?? item["attestations"];
            if (envs != null) Console.WriteLine($"         {envs.ToJsonString()}");
        }
        return 0;
    }

    // ==================== shared plumbing ====================

    private static string Path(string suffix) => $"/repos/{LedgerClient.RepoId}/{suffix}";

    /// <summary>Emit by status: 2xx prints the server projection (or the
    /// success line); anything else is an error on stderr with the server's
    /// body verbatim (schema hints survive). JSON mode (F6 + round-2 F-D/G-b):
    /// the server body must PARSE as JSON before it can claim ok — parsed
    /// nodes go into the envelope's data (so .data.count and
    /// --filter-output work); a 2xx with a non-JSON body is a system error on
    /// BOTH faces (stderr + rc 2), never an ok:true string-wrap.</summary>
    private static int Emit(int status, string resp, string? humanLine, bool json = false)
    {
        if (status is >= 200 and < 300)
        {
            if (json)
            {
                var body = resp.Trim();
                if (body.Length == 0)
                {
                    Console.WriteLine(OutputFormatter.WrapEnvelopeText(humanLine ?? "ok"));
                    return 0;
                }
                try
                {
                    // Validate BEFORE wrapping: WrapEnvelope itself tolerates
                    // non-JSON (embeds it as a string), which is exactly the
                    // false-ok this path must not produce.
                    _ = JsonNode.Parse(body) ?? (object)"null";
                    Console.WriteLine(OutputFormatter.WrapEnvelope(body));
                    return 0;
                }
                catch (JsonException)
                {
                    Console.Error.WriteLine($"ledger response not valid JSON: '{body[..Math.Min(60, body.Length)]}...'");
                    return 2; // system error — mirror the human face's verdict
                }
            }
            if (humanLine != null) Console.WriteLine(humanLine);
            if (!string.IsNullOrWhiteSpace(resp))
                Console.WriteLine(resp.Trim());
            return 0;
        }
        Console.Error.WriteLine($"ledger {status}: {resp.Trim()}");
        if (json && !string.IsNullOrWhiteSpace(resp))
        {
            // G-4: same parsed-data shape as the success face (WrapEnvelope),
            // so consumers handle ONE envelope form for both verdicts.
            try { _ = JsonNode.Parse(resp.Trim()); Console.WriteLine(OutputFormatter.WrapEnvelope(resp.Trim(), success: false)); }
            catch (JsonException) { /* stderr already carries the verbatim body */ }
        }
        if (status == 401) Console.Error.WriteLine("(signature/key: check the local Ed25519 key and that the CLI's embedded public key is registered for this repo on ledger.ohmygh.com)");
        if (status == 429) Console.Error.WriteLine("(per-key quota is 50 writes / UTC day; idempotent replays do not consume it)");
        return 1;
    }

    /// <summary>Single-pass args parser (F4 + round-2 F-A): flag tokens consume
    /// their value token (or an inline --flag=value); anything else is
    /// positional. A null setter marks a boolean flag. Dash tokens that match
    /// NO registered flag are USAGE ERRORS, not positionals — before the F4
    /// fix they stole the positional slot; after it they landed in it.</summary>
    private static List<string> ParseArgs(
        string[] a, params (string Name, Action<string>? Set)[] flags)
    {
        var positionals = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            var token = a[i];
            // G-2: POSIX end-of-options — everything after a bare `--` is
            // positional, so dash-leading titles/ids stay passable.
            if (token == "--")
            {
                positionals.AddRange(a[(i + 1)..]);
                return positionals;
            }
            // inline --flag=value form
            string? inline = null;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = token.IndexOf('=');
                if (eq > 2) { inline = token[(eq + 1)..]; token = token[..eq]; }
            }
            var matched = false;
            foreach (var (name, set) in flags)
            {
                if (!string.Equals(token, name, StringComparison.Ordinal)) continue;
                matched = true;
                if (set == null)
                {
                    if (inline != null)
                        throw new InvalidOperationException($"{name} is a boolean flag; use bare {name}.");
                    break;
                }
                if (inline != null) { set(inline); break; }
                if (i + 1 >= a.Length) throw new InvalidOperationException($"{name} needs a value.");
                set(a[++i]);
                break;
            }
            if (matched) continue;
            if (token.StartsWith('-') && token.Length > 1)
                throw new InvalidOperationException($"unknown option '{a[i]}'.");
            positionals.Add(a[i]);
        }
        return positionals;
    }

    private static List<JsonObject> Items(JsonNode? root, string arrayKey)
    {
        if (root is JsonArray arr) return arr.OfType<JsonObject>().ToList();
        if (root is JsonObject obj)
        {
            foreach (var key in new[] { arrayKey, "items", "data" })
                if (obj[key] is JsonArray a2)
                    return a2.OfType<JsonObject>().ToList();
        }
        return [];
    }

    private static bool HasMore(JsonNode? root) =>
        root is JsonObject o && o["has_more"] is JsonValue v && (v.ToString() == "1" || v.ToString() == "true");

    private static string? Field(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject o) return null;
        foreach (var n in names)
            if (o[n] is { } v)
                return v.ToString();
        return null;
    }

    // ==================== usage faces ====================

    private static void UsageIssue()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  officecli issue new \"<title>\" --kind bug|improvement --acceptance \"<criteria>\"");
        Console.WriteLine("      Open an issue on the repo ledger (kind defaults to bug)");
        Console.WriteLine("  officecli issue list [--limit N] [--before <id>] [--json]");
        Console.WriteLine("      Family pagination: pages of <=100, has_more saturation hint");
        Console.WriteLine("  officecli issue show <n> [--json]");
        Console.WriteLine("      One issue with its event history");
        Console.WriteLine("Closing/status changes are NOT in this CLI (additive-only surface): the");
        Console.WriteLine("dev workbench delegates them to omc (omc ledger issue status <repo> <n> <to>).");
        Console.WriteLine("Values starting with '-': use --flag=value, --flag -x, or a bare '--' before them.");
        Console.WriteLine();
        Console.WriteLine($"Source of truth: {LedgerClient.ApiBase} (REQ-063 ledger; supersedes issues.ohmygh.com).");
        Console.WriteLine("Repo: " + LedgerClient.RepoId);
    }

    private static void UsageArtifact()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  officecli artifact publish --name <n> --kind <kind> --digest sha256:<64hex>");
        Console.WriteLine("      [--version <v>] [--git-range <a..b>] [--deps <id,id,...>] [--outcome success|failure] [--note <text>]");
        Console.WriteLine("  officecli artifact attest <id> --type attest_dev|attest_prod|verification_failed [--note <text>]");
        Console.WriteLine("  officecli artifact list [--current] [--env dev|prod] [--json]");
        Console.WriteLine();
        Console.WriteLine("Kinds: experience (outcome success|failure), lesson, research, prototype, binary,");
        Console.WriteLine("       image, wasm, sbom, schema, openapi, eval-set, benchmark, runbook, decision, attested-report");
        Console.WriteLine("Digest = sha256 of the content/record (metadata registry; bytes stay out).");
        Console.WriteLine("Promote/demote/supersede are NOT in this CLI (additive-only surface):");
        Console.WriteLine("they run through the omc workbench (herdr-delegated).");
        Console.WriteLine($"Source of truth: {LedgerClient.ApiBase} (REQ-063).");
    }
}
