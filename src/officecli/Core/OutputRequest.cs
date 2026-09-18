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
    public string Command { get; init; } = "";

    /// <summary>Machine mode is implied by any non-toon format or by filtering/full:
    /// the envelope post-processors only make sense over the machine envelope.</summary>
    public bool NeedsRewrite => Format is "yaml" or "md" || FilterKeys.Length > 0 || Full;

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
            if (a == "--format" && i + 1 < args.Length)
            {
                format = args[++i];
                continue;
            }
            if (a == "--filter-output" && i + 1 < args.Length)
            {
                filter = args[++i];
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
            if (count > 0) foreach (var c in buffer[index..(index + count)]) { if (!_decided) Decide(c); }
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

        /// <summary>Transform and emit the buffered machine envelope. Called once at
        /// process end by Program; also wired into Flush for safety.</summary>
        public void FlushFinal()
        {
            if (!_machine || _buffer == null) { Flush(); return; }
            _machine = false;
            var text = _buffer.ToString();
            _buffer.Clear();
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

        private string Transform(JsonObject envelope)
        {
            if (_request.Full)
            {
                var meta = envelope["meta"] as JsonObject ?? new JsonObject();
                meta["command"] = _request.Command;
                meta["duration_ms"] = _clock.ElapsedMilliseconds;
                envelope["meta"] = meta;
            }
            if (_request.FilterKeys.Length > 0)
                envelope["data"] = FilterPaths(envelope, _request.FilterKeys);

            return _request.Format switch
            {
                "yaml" => ToYaml(envelope, 0),
                "md" => ToMarkdown(envelope),
                _ => envelope.ToJsonString(OutputFormatter.PublicJsonOptions) + Environment.NewLine,
            };
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
                    // A list-item object puts its first key on the dash line; its
                    // continuation keys align two spaces in (indent+2), matching
                    // the nested-object case below.
                    var first = listItem;
                    var contPad = listItem ? new string(' ', indent + 2) : pad;
                    foreach (var (key, value) in obj)
                    {
                        if (first) { sb.Append(pad).Append("- "); first = false; }
                        else sb.Append(contPad);
                        sb.Append(YamlKey(key)).Append(':');
                        WriteYamlValue(sb, value, indent + 2);
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

        private static void WriteMdNode(StringBuilder sb, JsonNode node, int depth)
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

        private static string MdCell(JsonNode? cell) =>
            (cell?.ToString() ?? "null").Replace("|", "\\|").Replace("\n", " ");
    }
}
