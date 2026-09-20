// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace OfficeCli.Core.Ledger;

/// <summary>
/// Signing client for ledger.ohmygh.com (REQ-063 仓级公共账本, the strict
/// superset of the retired issues.ohmygh.com face). Writes are Ed25519-signed
/// with five mandatory headers; reads are plain GETs (rate-limit-free per the
/// contract). Idempotency: a fresh key per logical write; network retries of
/// the SAME body reuse the key (server replays the first result); changing the
/// body under the same key is a client bug the server answers with 409.
/// </summary>
internal static class LedgerClient
{
    public const string RepoId = "github.com/raystyle/OfficeCLI";
    public const string DefaultApiBase = "https://ledger.ohmygh.com";

    // Identity distribution face (REQ-063 裁 2): the public key ships as a
    // constant inside the CLI; the private key lives only on the commit side
    // (env OFFICECLI_LEDGER_KEY / OFFICECLI_LEDGER_KEY_FILE, default
    // ~/.officecli/ledger/officecli-ed25519.key — never in argv, never in the
    // repo, never in CI logs).
    public const string PublicKeyJwk =
        "{\"crv\":\"Ed25519\",\"kty\":\"OKP\",\"x\":\"nJSAMQ0jxm0YwvWiekw5iKc5a0pMvFRwe11Z9gEj6qw\"}";
    public const string KeyId = "fcb406f89de20a075441af3a6b5dbe3c26c0b6f8f804a8fbb455ff9758b1e50a";

    public static string ApiBase =>
        Environment.GetEnvironmentVariable("OFFICECLI_LEDGER_API") ?? DefaultApiBase;

    internal static readonly HashSet<string> IssueKinds =
        new(StringComparer.OrdinalIgnoreCase) { "bug", "improvement" };

    internal static readonly HashSet<string> ArtifactKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "experience", "lesson", "research", "prototype", "binary", "image", "wasm",
            "sbom", "schema", "openapi", "eval-set", "benchmark", "runbook", "decision",
            "attested-report",
        };

    // EventTypes (claim/release/status/result/blocker) retired with the
    // additive-only surface: no CLI face posts events any more.

    /// <summary>Additive-only attestation faces (总台修正令 2026-09-20);
    /// promote/demote/supersede run through the omc workbench.</summary>
    internal static readonly HashSet<string> AttestationTypes =
        new(StringComparer.OrdinalIgnoreCase)
        { "attest_dev", "attest_prod", "verification_failed" };

    /// <summary>Digest shape the ledger accepts: sha256 + colon + 64 lowercase hex.</summary>
    internal static bool IsValidDigest(string? digest) =>
        digest != null && System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$");

    // ==================== signing ====================

    /// <summary>
    /// Signing base (v1): six newline-joined lines — tag, method, url.pathname,
    /// unix timestamp, nonce, idempotency key, hex sha256 of the exact body
    /// bytes sent. Internal for the __selftest__ unit vector.
    /// </summary>
    internal static string BuildSigningBase(string method, string pathname,
        long timestamp, string nonce, string idempotencyKey, string bodySha256Hex) =>
        "v1\n" + method + "\n" + pathname + "\n" + timestamp + "\n" + nonce + "\n"
        + idempotencyKey + "\n" + bodySha256Hex;

    internal static string ComputeBodySha256Hex(byte[] body) =>
        Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    /// <summary>Load the Ed25519 seed: env value (hex/base64/base64url of the
    /// 32-byte seed), else a key file (env path, default local keyfile). An
    /// EXPLICIT env value that does not parse is an error, not a silent
    /// fallback — the caller said which key to use.</summary>
    internal static Ed25519PrivateKeyParameters? TryLoadPrivateKey()
    {
        var key = Environment.GetEnvironmentVariable("OFFICECLI_LEDGER_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            return ParseSeed(key.Trim())
                ?? throw new InvalidOperationException(
                    "OFFICECLI_LEDGER_KEY is set but is not a 32-byte Ed25519 seed "
                    + "(hex or base64). Fix or unset it — explicit keys never fall back.");
        }
        var file = Environment.GetEnvironmentVariable("OFFICECLI_LEDGER_KEY_FILE")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       ".officecli", "ledger", "officecli-ed25519.key");
        try
        {
            if (File.Exists(file))
            {
                var p = ParseSeed(File.ReadAllText(file).Trim());
                if (p != null) return p;
                throw new InvalidOperationException(
                    $"ledger key file {file} does not contain a 32-byte Ed25519 seed (hex or base64).");
            }
        }
        catch (InvalidOperationException) { throw; }
        catch { /* unreadable file falls through to null */ }
        return null;
    }

    private static Ed25519PrivateKeyParameters? ParseSeed(string text)
    {
        byte[]? seed = null;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-fA-F]{64}$"))
            seed = Convert.FromHexString(text);
        else
        {
            try
            {
                var b64 = text.Replace('-', '+').Replace('_', '/');
                switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length == 32) seed = bytes;
            }
            catch { /* not base64 either */ }
        }
        return seed is { Length: 32 }
            ? new Ed25519PrivateKeyParameters(seed)
            : null;
    }

    // ==================== transport ====================

    private static HttpClient Client()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"officecli/{LedgerCli.SelfVersion()} ledger");
        return c;
    }

    /// <summary>Signed POST; returns (status, body). The server's error body is
    /// surfaced verbatim so schema hints from real-fire runs reach the caller.
    /// Idempotency: a fresh key per logical write; an explicit
    /// <paramref name="idempotencyKey"/> is reserved for content-derived
    /// replays (a retry must replay rather than append) — never reuse a key
    /// across different bodies.</summary>
    internal static async Task<(int Status, string Body)> PostAsync(
        string pathname, JsonObject body, string? idempotencyKey = null)
    {
        var payload = body.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(payload);
        var idem = idempotencyKey ?? Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N")[..16];
        var baseString = BuildSigningBase("POST", pathname, ts, nonce, idem,
            ComputeBodySha256Hex(bytes));

        var priv = TryLoadPrivateKey()
            ?? throw new InvalidOperationException(
                "no ledger signing key: set OFFICECLI_LEDGER_KEY (seed) or OFFICECLI_LEDGER_KEY_FILE "
                + "(default ~/.officecli/ledger/officecli-ed25519.key). The private key never enters "
                + "argv or the repo.");

        var signer = new Ed25519Signer();
        signer.Init(true, priv);
        var baseBytes = Encoding.UTF8.GetBytes(baseString);
        signer.BlockUpdate(baseBytes, 0, baseBytes.Length);
        var signature = Convert.ToBase64String(signer.GenerateSignature())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var c = Client();
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + pathname)
        {
            Content = new ByteArrayContent(bytes),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("Idempotency-Key", idem);
        req.Headers.Add("X-Key-Id", KeyId);
        req.Headers.Add("X-Timestamp", ts.ToString());
        req.Headers.Add("X-Nonce", nonce);
        req.Headers.Add("X-Signature", signature);
        using var resp = await c.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        if (resp.Headers.TryGetValues("Retry-After", out var ra))
            respBody += $"\n(Retry-After: {string.Join(",", ra)})";
        return ((int)resp.StatusCode, respBody);
    }

    internal static async Task<(int Status, string Body)> GetAsync(string pathnameAndQuery)
    {
        using var c = Client();
        using var resp = await c.GetAsync(ApiBase + pathnameAndQuery);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
}
