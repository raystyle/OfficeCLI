// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfficeCli.Core;

/// <summary>
/// cli-docs 输出协议旗标族(issue #14 wave a,纯增量):--format、--filter-output、
/// --full-output 三件在 Program 层抽取——不逐命令注册(命令树零改动),客户端改写后
/// 再进 SCL/early-dispatch/resident 转发,故常驻与单跑行为一致。机器形信封在
/// Console.Out 出口由 <see cref="Rewriter"/> 统一后处理:格式转换(json/yaml/md)、
/// data 键路径过滤、meta 补全(command/duration_ms/cta)。非 JSON 输出(人类形、
/// html、流式 watch)首字节探测后原样透传,不缓冲。
/// </summary>
internal sealed class OutputRequest
{
    public string Format { get; init; } = "toon";      // toon|json|yaml|md
    public string[] FilterKeys { get; init; } = [];    // envelope-rooted paths: data.x,data.a[0].y
    public bool Full { get; init; }                    // --full-output: envelope + meta

    /// <summary>OFFICECLI_ENVELOPE=strict preview (issue #14 wave b enabler):
    /// normalize the compat envelope to the cli-docs target shape — drop
    /// success/message, fold the error object into a human string + a
    /// single-line {code, message, cta?} on stderr. Compat stays the default
    /// until the cross-repo consumer flip (wave b) and success retirement
    /// (wave c). Mutable: Program ORs it into a flag-derived request.</summary>
    public bool Strict { get; set; }

    public string Command { get; init; } = "";

    /// <summary>Machine mode is implied by any non-toon format or by filtering/full:
    /// the envelope post-processors only make sense over the machine envelope.</summary>
    public bool NeedsRewrite => Format is "yaml" or "md" || FilterKeys.Length > 0 || Full || Strict;

    /// <summary>
    /// Extract the flag family from the raw CLI args (exact tokens only, any
    /// position) and return the rewritten args: --format json|yaml|md becomes a
    /// literal --json so the command emits the machine envelope; --format toon,
    /// --filter-output and --full-output are stripped (their semantics live in the
    /// rewriter, not in any command). A token equal to these flag names appearing
    /// where an option VALUE was expected is malformed input today (SCL parse
    /// error) — same accepted trade as the any-position --help rewrite.
    /// Returns null when no flag of the family is present (zero overhead path).
    /// </summary>
    public static OutputRequest? Extract(ref string[] args, Stopwatch? clock = null)
    {
        string? format = null;
        string? filter = null;
        bool full = false;
        var keep = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--full-output") { full = true; continue; }
            if (a == "--format" || a == "--filter-output")
            {
                // G7: a value-less family flag would fall through to SCL's
                // confusing "Unrecognized command or argument" — fail with the
                // real cause instead.
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"error: {a} needs a value" +
                        (a == "--format" ? " (toon|json|yaml|md)." : " (envelope-rooted key paths, comma-separated)."));
                    Environment.Exit(2);
                }
                if (a == "--format") format = args[++i];
                else filter = args[++i];
                continue;
            }
            keep.Add(a);
        }
        if (format == null && filter == null && !full) return null;

        format = format is null or "toon" ? "toon" : format;
        if (format is not ("toon" or "json" or "yaml" or "md"))
        {
            Console.Error.WriteLine($"error: unknown --format '{format}'. Valid: toon, json, yaml, md.");
            Environment.Exit(2);
        }

        var request = new OutputRequest
        {
            Format = format,
            FilterKeys = (filter ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Full = full,
            Command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "",
        };

        // Machine envelope needed for anything the rewriter does — inject --json
        // for json/yaml/md. For toon the command keeps its default human face
        // (an explicit --json the user passed still wins).
        if (format is "json" or "yaml" or "md") keep.Add("--json");
        args = keep.ToArray();
        return request;
    }

    /// <summary>Install the stdout rewriter for this request. Null-safe no-op when
    /// the request does not need post-processing (e.g. --format json/toon only).</summary>
    public Rewriter? Install()
    {
        if (!NeedsRewrite) return null;
        var r = new Rewriter(this);
        Console.SetOut(r);
        return r;
    }

    /// <summary>
    /// Console.Out rewriter: buffers while the output looks like the machine
    /// envelope (first non-ws byte '{'), transforms on flush, passes everything
    /// else through unbuffered. Process-end flush is driven by Program after
    /// invocation (rewriters never outlive one command).
    /// </summary>
    internal sealed class Rewriter : TextWriter
    {
        private readonly OutputRequest _request;
        private readonly TextWriter _inner;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly StringBuilder? _buffer;
        private bool _machine;   // buffering active
        private bool _decided;   // first byte seen

        public override Encoding Encoding => _inner.Encoding;

        public Rewriter(OutputRequest request)
        {
            _request = request;
            _inner = Console.Out;
            _machine = true;
            _buffer = new StringBuilder();
        }

        public override void Write(char value)
        {
            if (!_decided) Decide(value);
            if (_machine) { _buffer!.Append(value); return; }
            _inner.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            // G8: only the FIRST byte decides the face — no slice allocation.
            if (count > 0 && !_decided) Decide(buffer[index]);
            if (_machine) { _buffer!.Append(buffer, index, count); return; }
            _inner.Write(buffer, index, count);
        }

        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value) && !_decided) Decide(value[0]);
            if (_machine) { _buffer!.Append(value); return; }
            _inner.Write(value);
        }

        private void Decide(char c)
        {
            _decided = true;
            if (!char.IsWhiteSpace(c) && c != '{') _machine = false; // human/stream face: pass through
        }

        /// <summary>Transform and emit the buffered machine envelope. Called from
        /// the ProcessExit hook on every exit path (early dispatches return
        /// before the main parse). Idempotent: a second call is a no-op.</summary>
        public void FlushFinal()
        {
            if (!_machine || _buffer == null) { Flush(); return; }
            _machine = false;
            var text = _buffer.ToString();
            _buffer.Clear();
            // G9: ProcessExit can race a server thread still writing — the
            // swap+emit block is atomic on the inner writer.
            lock (_inner)
            {
                Console.SetOut(_inner); // restore before writing so nested writes can't loop
                if (text.Trim().Length == 0) { Flush(); return; }
                try
                {
                    var node = JsonNode.Parse(text.Trim());
                    if (node is JsonObject envelope)
                        Console.Out.Write(Transform(envelope));
                    else
                        Console.Out.Write(text); // not an envelope object after all
                }
                catch (JsonException)
                {
                    Console.Out.Write(text); // not JSON — hand through untouched
                }
                Flush();
            }
        }

        private string Transform(JsonObject envelope)
        {
            if (_request.Strict) NormalizeStrict(envelope);
            if (_request.Full)
            {
                var meta = envelope["meta"] as JsonObject ?? new JsonObject();
                meta["command"] = _request.Command;
                meta["duration_ms"] = _clock.ElapsedMilliseconds;
                envelope["meta"] = meta;
            }
            if (_request.FilterKeys.Length > 0)
            {
                var filtered = FilterPaths(envelope, _request.FilterKeys);
                // G7: a fully-empty pick means every key missed — say so on
                // stderr instead of an ambiguous silent data:{}.
                if (filtered is JsonObject picks && picks.Count == 0)
                    Console.Error.WriteLine("filter: no keys matched (paths are envelope-rooted, e.g. data.results[0].path).");
                envelope["data"] = filtered;
            }

            return _request.Format switch
            {
                "yaml" => ToYaml(envelope, 0),
                "md" => ToMarkdown(envelope),
                _ => envelope.ToJsonString(OutputFormatter.PublicJsonOptions) + Environment.NewLine,
            };
        }

        /// <summary>Strict-shape normalization (wave b preview): success/message
        /// drop (ok is the verdict); the structured error object folds into a
        /// human-readable `error` string on stdout plus a single-line
        /// {code, message, cta?} JSON on stderr (cli-docs 错误分道). F2: a
        /// message-ONLY failure envelope (WrapEnvelopeError — no error object)
        /// also folds into the error slot, so the verdict never disappears on
        /// either channel. The stderr line is hand-composed — no serializer —
        /// so the trimmed publish cannot lose it to reflection stripping.</summary>
        private static void NormalizeStrict(JsonObject envelope)
        {
            var messageOnly = envelope["message"]?.ToString();
            envelope.Remove("success");
            envelope.Remove("message");

            if (envelope["error"] is JsonObject err)
            {
                var message = err["error"]?.ToString() ?? "";
                var code = err["code"]?.ToString() ?? "";
                var cta = envelope["meta"]?["cta"] as JsonObject;
                envelope["error"] = message;
                WriteStderrErrorLine(code, message, cta);
                return;
            }

            var failed = envelope["ok"] is JsonValue okv && okv.GetValue<bool>() == false;
            if (failed && !string.IsNullOrWhiteSpace(messageOnly))
            {
                envelope["error"] = messageOnly;
                WriteStderrErrorLine("", messageOnly, null);
            }
        }

        private static void WriteStderrErrorLine(string code, string message, JsonObject? cta)
        {
            var line = new StringBuilder("{\"code\":").Append(QuoteJson(code))
                .Append(",\"message\":").Append(QuoteJson(message));
            if (cta?["description"] is { } desc)
            {
                line.Append(",\"cta\":{\"description\":").Append(QuoteJson(desc.ToString()));
                if (cta["commands"] is JsonArray cmds && cmds.Count > 0
                    && cmds[0] is JsonObject first)
                {
                    var command = first["command"]?.ToString() ?? "";
                    var description = first["description"]?.ToString();
                    line.Append(",\"commands\":[{\"command\":").Append(QuoteJson(command));
                    if (description != null)
                        line.Append(",\"description\":").Append(QuoteJson(description));
                    line.Append("}]"); // close the command object, then the array
                }
                line.Append('}'); // close cta
            }
            line.Append('}'); // close root
            Console.Error.WriteLine(line.ToString());
        }

        /// <summary>Key-path filter rooted at the ENVELOPE: paths like
        /// "data.matches,data.results[0].text" (dot members, [n] array index).
        /// Only hits are kept (keyed by their full path); misses are skipped.</summary>
        private static JsonNode? FilterPaths(JsonNode data, string[] paths)
        {
            var result = new JsonObject();
            foreach (var path in paths)
            {
                var node = data;
                var name = path;
                foreach (var seg in SplitPath(path))
                {
                    if (seg.index is int i)
                    {
                        if (node is JsonArray arr && i >= 0 && i < arr.Count) node = arr[i];
                        else { node = null; break; }
                    }
                    else
                    {
                        if (node is JsonObject obj && obj.ContainsKey(seg.name!)) node = obj[seg.name!];
                        else { node = null; break; }
                    }
                }
                if (node != null) result[name] = node.DeepClone();
            }
            return result;
        }

        private static IEnumerable<(string? name, int? index)> SplitPath(string path)
        {
            foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = raw;
                while (seg.Length > 0 && seg[^1] == ']')
                {
                    var open = seg.LastIndexOf('[');
                    if (open < 0) break;
                    var idxText = seg[(open + 1)..^1];
                    if (!int.TryParse(idxText, out var idx)) break;
                    if (open > 0) yield return (seg[..open], null);
                    yield return (null, idx);
                    seg = "";
                }
                if (seg.Length > 0) yield return (seg, null);
            }
        }

        // ----- minimal deterministic YAML renderer (JsonNode → text) -----

        private static string ToYaml(JsonNode node, int indent)
        {
            var sb = new StringBuilder();
            WriteYaml(sb, node, indent, root: true);
            return sb.ToString();
        }

        private static void WriteYaml(StringBuilder sb, JsonNode node, int indent, bool root = false, bool listItem = false)
        {
            var pad = new string(' ', indent);
            switch (node)
            {
                case JsonObject obj:
                    if (!root && !listItem) sb.AppendLine();
                    // A list-item object puts its first key on the dash line
                    // ("pad- "), continuation keys at indent+2. F3: a NESTED
                    // container under a key must be strictly deeper than its
                    // key's column — dash-line keys sit at indent+2 (children
                    // at indent+2), continuation keys at indent+2 (children at
                    // indent+4); using indent+2 for both produced siblings at
                    // the key's own column and flattened the tree.
                    var first = listItem;
                    var contPad = listItem ? new string(' ', indent + 2) : pad;
                    foreach (var (key, value) in obj)
                    {
                        var valueIndent = first ? indent + 2 : indent + 4;
                        if (first) { sb.Append(pad).Append("- "); first = false; }
                        else sb.Append(contPad);
                        sb.Append(YamlKey(key)).Append(':');
                        WriteYamlValue(sb, value, valueIndent);
                    }
                    break;
                case JsonArray arr:
                    if (!root && !listItem) sb.AppendLine();
                    foreach (var item in arr)
                    {
                        if (item is JsonObject)
                        {
                            WriteYaml(sb, item, indent, root: true, listItem: true);
                        }
                        else
                        {
                            sb.Append(pad).Append("- ");
                            WriteYamlValue(sb, item, indent + 2);
                        }
                    }
                    break;
                default:
                    sb.Append(pad).AppendLine(YamlScalar(node));
                    break;
            }
        }

        private static void WriteYamlValue(StringBuilder sb, JsonNode? value, int indent)
        {
            switch (value)
            {
                case null:
                    sb.AppendLine(" null");
                    break;
                case JsonObject nestedObj when nestedObj.Count == 0:
                    sb.AppendLine(" {}"); // F3: empty collections must not read as null
                    break;
                case JsonArray nestedArr when nestedArr.Count == 0:
                    sb.AppendLine(" []");
                    break;
                case JsonObject:
                case JsonArray:
                    WriteYaml(sb, value, indent); // emits its own leading newline
                    break;
                default:
                    sb.Append(' ').AppendLine(YamlScalar(value));
                    break;
            }
        }

        private static string YamlKey(string key)
        {
            return key.IndexOfAny(":#{}[],&*?|<>=!%@`\"' \t".ToCharArray()) >= 0
                ? QuoteJson(key)
                : key;
        }

        private static string YamlScalar(JsonNode node)
        {
            return node is JsonValue v
                ? v.TryGetValue<string>(out var s) ? YamlString(s) : v.ToString()
                : node.ToString();
        }

        private static string YamlString(string s)
        {
            if (s.Length == 0 || s.IndexOfAny("\n\r\t:#{}[],&*?|<>=!%@`\"'".ToCharArray()) >= 0 || char.IsWhiteSpace(s[0]))
                return QuoteJson(s);
            // F3: quote strings a YAML loader would coerce to non-strings —
            // numbers (incl. leading-zero octal-looking ids like "00100000"),
            // floats, bools and nulls (YAML 1.1 widens yes/no/on/off too).
            if (System.Text.RegularExpressions.Regex.IsMatch(s,
                    @"^(?:[-+]?(\d[\d_]*|0[xXoObB][0-9a-fA-F_]+)|[-+]?(\d*\.\d+|\d+\.\d*)([eE][-+]?\d+)?|[-+]?\d+[eE][-+]?\d+)$")
                || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || s.Equals("false", StringComparison.OrdinalIgnoreCase)
                || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || s.Equals("no", StringComparison.OrdinalIgnoreCase)
                || s.Equals("on", StringComparison.OrdinalIgnoreCase)
                || s.Equals("off", StringComparison.OrdinalIgnoreCase)
                || s.Equals("null", StringComparison.OrdinalIgnoreCase)
                || s == "~")
                return QuoteJson(s);
            return s;
        }

        /// <summary>JSON string quoting without the serializer (this publish is
        /// trimmed: reflection-based JsonSerializer.Serialize(string) throws).</summary>
        private static string QuoteJson(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ----- minimal deterministic Markdown renderer (envelope → md) -----

        private static string ToMarkdown(JsonObject envelope)
        {
            var sb = new StringBuilder();
            foreach (var (key, value) in envelope)
            {
                if (value is JsonObject or JsonArray)
                {
                    sb.Append("## ").Append(key).AppendLine().AppendLine();
                    WriteMdNode(sb, value, 0);
                    sb.AppendLine();
                }
                else
                {
                    sb.Append("- **").Append(key).Append("**: ").AppendLine(value?.ToString() ?? "null");
                }
            }
            return sb.ToString();
        }

        private static void WriteMdNode(StringBuilder sb, JsonNode? node, int depth)
        {
            var pad = new string(' ', depth * 2);
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (value is JsonObject or JsonArray)
                        {
                            sb.Append(pad).Append("- **").Append(key).AppendLine("**:");
                            WriteMdNode(sb, value, depth + 1);
                        }
                        else
                        {
                            sb.Append(pad).Append("- ").Append(key).Append(": ")
                              .AppendLine(value?.ToString() ?? "null");
                        }
                    }
                    break;
                case JsonArray arr when arr.All(i => i is JsonObject):
                    var cols = arr.OfType<JsonObject>()
                        .SelectMany(o => o.Select(kv => kv.Key))
                        .Distinct()
                        .ToList();
                    if (cols.Count > 0)
                    {
                        sb.Append(pad).Append("| ").Append(string.Join(" | ", cols)).AppendLine(" |");
                        sb.Append(pad).Append("|").AppendLine(string.Concat(Enumerable.Repeat("---|", cols.Count)));
                        foreach (var item in arr.OfType<JsonObject>())
                        {
                            var cells = cols.Select(c => MdCell(item[c]));
                            sb.Append(pad).Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
                        }
                    }
                    break;
                case JsonArray arr:
                    foreach (var item in arr) WriteMdNode(sb, item, depth);
                    break;
                default:
                    sb.Append(pad).AppendLine(node?.ToString() ?? "null");
                    break;
            }
        }

        /// <summary>Table cell: nested objects/arrays render as COMPACT JSON
        /// (G10 — the default node ToString indents and \u002B-escapes), pipes
        /// escaped, newlines flattened.</summary>
        private static string MdCell(JsonNode? cell)
        {
            var text = cell switch
            {
                JsonObject or JsonArray => cell.ToJsonString(OutputFormatter.PublicJsonOptions),
                null => "null",
                _ => cell.ToString(),
            };
            return text.Replace("|", "\\|").Replace("\n", " ");
        }
    }
}
