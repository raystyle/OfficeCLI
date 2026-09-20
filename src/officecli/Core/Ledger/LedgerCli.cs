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
                    case "close": return IssueClose(rest);
                }
            }
            else
            {
                switch (cmd)
                {
                    case "": case "help": UsageArtifact(); return 0;
                    case "publish": return ArtifactPublish(rest);
                    case "attest": return ArtifactAttest(rest, null);
                    case "promote": return ArtifactAttest(rest, "promote");
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
        var (pos, _) = ParseArgs(a,
            ("--kind", v => kind = v), ("--acceptance", v => acceptance = v),
            ("--body", v => acceptance = acceptance == null ? v : acceptance + "\n" + v));
        var title = pos.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("issue new needs a title: officecli issue new \"<title>\" --kind bug|improvement --acceptance \"<criteria>\"");
        kind ??= "bug";
        if (!LedgerClient.IssueKinds.Contains(kind))
            throw new InvalidOperationException($"unknown issue kind '{kind}'. Valid: bug, improvement.");
        if (string.IsNullOrWhiteSpace(acceptance))
            throw new InvalidOperationException("issue new needs --acceptance \"<criteria>\" (the completion judgment pairs with a result digest at close time).");

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
            ("--before", v => before = v));
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
        var (pos, _) = ParseArgs(a, ("--json", null));
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

    private static int IssueClose(string[] a)
    {
        string? digest = null, note = null;
        var (pos, _) = ParseArgs(a, ("--digest", v => digest = v), ("--note", v => note = v), ("--json", null));
        var n = pos.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(n) || !int.TryParse(n, out _))
            throw new InvalidOperationException("issue close needs an issue number: officecli issue close <n> --digest sha256:<64hex>");
        if (!LedgerClient.IsValidDigest(digest))
            throw new InvalidOperationException("issue close needs --digest sha256:<64hex> referencing a REGISTERED artifact (publish first; the ledger closes only on verified results).");

        var result = new JsonObject { ["type"] = "result", ["digest"] = digest };
        if (note != null) result["note"] = note;
        // G4: the result event uses an idempotency key DERIVED from (issue,
        // digest) — re-running a half-finished close replays the same event
        // instead of appending a duplicate to the append-only ledger.
        var (s1, r1) = LedgerClient.PostAsync(Path($"issues/{n}/events"), result,
            idempotencyKey: ResultEventKey(n, digest!)).GetAwaiter().GetResult();
        if (s1 is < 200 or >= 300) return Emit(s1, r1, null, json: a.Contains("--json"));

        var done = new JsonObject { ["type"] = "status", ["to"] = "done" };
        var (s2, r2) = LedgerClient.PostAsync(Path($"issues/{n}/events"), done).GetAwaiter().GetResult();
        if (s2 is < 200 or >= 300)
        {
            Console.Error.WriteLine($"half-state: the result event for issue #{n} IS recorded (digest {digest}), but the status=done event failed — re-run the same close command to finish.");
            return Emit(s2, r2, null, json: a.Contains("--json"));
        }
        return Emit(s2, r2, $"Issue #{n}: result recorded, status done.", json: a.Contains("--json"));
    }

    /// <summary>Stable idempotency key for the close-chain result event (G4):
    /// same issue + same digest replays the same event on retry.</summary>
    private static string ResultEventKey(string n, string digest)
    {
        var seed = $"result:{LedgerClient.RepoId}:{n}:{digest}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed)))[..32].ToLowerInvariant();
    }

    // ==================== artifact ====================

    private static int ArtifactPublish(string[] a)
    {
        string? name = null, kind = null, digest = null, version = null, range = null, outcome = null, note = null;
        List<string>? deps = null;
        var (_, _) = ParseArgs(a,
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

    private static int ArtifactAttest(string[] a, string? fixedType)
    {
        string? type = fixedType, note = null;
        var (pos, _) = ParseArgs(a, ("--type", v => type = v), ("--note", v => note = v), ("--json", null));
        var id = pos.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"artifact {(fixedType ?? "attest")} needs an artifact id.");
        if (type == null || !LedgerClient.AttestationTypes.Contains(type))
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
    /// body verbatim (schema hints survive). JSON mode (F6) wraps the server
    /// body in the standard CLI envelope and suppresses the human line — the
    /// machine face must be parseable JSON.</summary>
    private static int Emit(int status, string resp, string? humanLine, bool json = false)
    {
        if (status is >= 200 and < 300)
        {
            if (json)
            {
                Console.WriteLine(OutputFormatter.WrapEnvelopeText(
                    string.IsNullOrWhiteSpace(resp) ? (humanLine ?? "ok") : resp.Trim()));
                return 0;
            }
            if (humanLine != null) Console.WriteLine(humanLine);
            if (!string.IsNullOrWhiteSpace(resp))
                Console.WriteLine(resp.Trim());
            return 0;
        }
        Console.Error.WriteLine($"ledger {status}: {resp.Trim()}");
        if (json && !string.IsNullOrWhiteSpace(resp))
            Console.WriteLine(OutputFormatter.WrapEnvelopeText(resp.Trim(), success: false));
        if (status == 401) Console.Error.WriteLine("(signature/key: check the local Ed25519 key and that the CLI's embedded public key is registered for this repo on ledger.ohmygh.com)");
        if (status == 429) Console.Error.WriteLine("(per-key quota is 50 writes / UTC day; idempotent replays do not consume it)");
        return 1;
    }

    /// <summary>Single-pass args parser (F4): flag tokens consume their value
    /// token, everything else is positional — flag VALUES can never be
    /// mistaken for positionals. A null setter marks a boolean flag (no
    /// value); unknown dash-tokens fall through as positionals and surface
    /// later as unknown-subcommand/parameter errors.</summary>
    private static (List<string> Positionals, int Consumed) ParseArgs(
        string[] a, params (string Name, Action<string>? Set)[] flags)
    {
        var positionals = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            var matched = false;
            foreach (var (name, set) in flags)
            {
                if (!string.Equals(a[i], name, StringComparison.Ordinal)) continue;
                matched = true;
                if (set == null) break; // boolean flag — no value token
                if (i + 1 >= a.Length) throw new InvalidOperationException($"{name} needs a value.");
                set(a[++i]);
                break;
            }
            if (!matched) positionals.Add(a[i]);
        }
        return (positionals, a.Length);
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
        Console.WriteLine("  officecli issue close <n> --digest sha256:<64hex> [--note <text>]");
        Console.WriteLine("      Close chain: result event citing a registered digest, then status=done");
        Console.WriteLine();
        Console.WriteLine($"Source of truth: {LedgerClient.ApiBase} (REQ-063 ledger; supersedes issues.ohmygh.com).");
        Console.WriteLine("Repo: " + LedgerClient.RepoId);
    }

    private static void UsageArtifact()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  officecli artifact publish --name <n> --kind <kind> --digest sha256:<64hex>");
        Console.WriteLine("      [--version <v>] [--git-range <a..b>] [--deps <id,id,...>] [--outcome success|failure] [--note <text>]");
        Console.WriteLine("  officecli artifact attest <id> --type attest_dev|attest_prod|verification_failed|demote|supersede [--note <text>]");
        Console.WriteLine("  officecli artifact promote <id>");
        Console.WriteLine("      sugar for attest --type promote");
        Console.WriteLine("  officecli artifact list [--current] [--env dev|prod] [--json]");
        Console.WriteLine();
        Console.WriteLine("Kinds: experience (outcome success|failure), lesson, research, prototype, binary,");
        Console.WriteLine("       image, wasm, sbom, schema, openapi, eval-set, benchmark, runbook, decision, attested-report");
        Console.WriteLine("Digest = sha256 of the content/record (metadata registry; bytes stay out).");
        Console.WriteLine($"Source of truth: {LedgerClient.ApiBase} (REQ-063).");
    }
}
