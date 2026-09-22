# Changelog

 Versions prior to this file live in
 [GitHub Releases](https://github.com/raystyle/OfficeCLI/releases) (auto-generated
 notes per tag).

## 1.0.161 (artifact publish 硬校验预检, 总台数据治理轮 2026-09-22)

- `artifact publish` now pre-checks the server's hard validation standard
  locally (总台 2026-09-22): kind is narrowed to the three-type standard
  experience | lesson | research (the retired 12 kinds are rejected before
  the network), `--summary` (the record's text body) is REQUIRED: empty
  publishes fail fast with rc 2 instead of a server 400. `--outcome`
  is required for every kind, success | failure only.
- `__selftest__` kind vectors updated: the three standard kinds must be
  present AND retired kinds (attested-report, prototype, runbook, ...)
  must be absent. Help faces (`help artifact`, usage, CommandBuilder tree,
  SKILL.md) updated to the same synopsis.
- Governance note: the four artifacts deleted in this round (empty-summary
  publishes under the old note-only face) are republished byte-identically
  with summary + outcome per the new standard; digests self-verify.

## 1.0.160 (权限收口, 总台修正令 2026-09-20)

- CLI ledger surface is ADDITIVE-ONLY now: `issue close` (result + status
  chain) and `artifact promote` are removed; `artifact attest` narrows to
  attest_dev | attest_prod | verification_failed (promote/demote/supersede
  dropped). Closures, deletions and status changes run exclusively through
  the omc workbench (`omc ledger issue status <repo> <n> <to>` /
  `omc ledger issue delete`), herdr-delegated from the dev workbench.
- Standard-code note: the fleet-unified ledger implementation is the
  ledger-rs crate (Rust; fleet pins tag **v0.1.1**: v0.1.0 has a
  URL-concatenation defect, 总台追注 2026-09-20). This repo is C#/.NET,
  where a Cargo dependency is mechanically inapplicable - the minimal C#
  signing client stays, pinned to the same five-header contract + kid
  convention, guarded by the __selftest__ signing-base/kid vectors
  (portability form referred back to the 总台).

## 1.0.159 (review-batch fixes)

- NOTE: the close-chain status flip is server-blocked today (the ledger's
  status-event validator rejects every `to` shape, probe evidence attached
  to the review; reported for the service side). `issue close` still records
  its idempotent result event; finishing an issue to `done` currently goes
  through the omc admin face. Strict mode adds no stderr line for batch
  partial failures by design (the per-item verdicts live in data.results).
- Ledger family (from the pre-push codex review): transport failures are
  caught (unreachable/timeout/bad-JSON to stderr + rc 2, was an rc-134 crash);
  single-pass arg parsing (flag VALUES can no longer be mistaken for the
  positional, which caused wrong-target writes); `--json` emits the standard
  envelope (was a human line + raw body); list ids project from
  `issue_n`/`artifact_id`; the close-chain result event carries a
  digest-derived idempotency key (retries replay instead of appending
  duplicates) and a half-state hint when status=done fails after the result.
- strict preview: message-only failure envelopes keep their verdict (folded
  into `error` + the stderr line); yaml renderer fixes (nested containers
  under continuation keys, scalar quoting for number/bool/null lookalikes,
  empty collections render `[]`/`{}`); filter misses say so on stderr;
  family flags without a value fail rc 2 with the real cause; md table cells
  render compact JSON.
- `--schema` honored at any position (value-position guarded) and unknown
  command names fail rc 2 instead of silently rendering the root schema.
- any-position `--help` rewrite is value-position guarded: a token right
  after a value-taking option stays that option's literal value (SCL
  behavior preserved: `set --find --help --replace x` really searches for
  "--help" instead of dumping help and silently editing nothing).
- `ok` mirror extended to batch item results, import merge, watch HTTP face,
  resident force-failure; env table teaches the ledger key vars; SKILL.md
  defect-reporting section moved to the ledger face (parity synced).

## 1.0.158

- `issue show` human projection fixed against the live detail face
  (`projection` + `timeline`, title/acceptance inside the issue_open event
  payload; `--json` was already complete). Real-fire verified: issue #1 +
  experience artifact registered on ledger.ohmygh.com.

## 1.0.157

- **issue face switched to the repo ledger** (REQ-063): `officecli issue
  new/list/show/close` now talks to ledger.ohmygh.com (append-only issue +
  artifact streams) instead of the retired issues.ohmygh.com tracker.
  New-unit shape: `issue new "<title>" --kind bug|improvement --acceptance
  "<criteria>"`; closing requires a `result` event citing a registered
  digest (`issue close <n> --digest sha256:...`), then `status=done`.
- **artifact command family** (new): `officecli artifact
  publish/attest/promote/list` registers shared-library products
  (experience, lesson, research, prototype, sbom, ...) by content digest,
  with dev/prod attestation and promote/demote/supersede chains.
- Writes are Ed25519-signed (five-header contract, idempotency keys,
  per-key quota 50/UTC-day); the public key ships in the CLI, the private
  key is read from env / local keyfile only. `__selftest__` now unit-checks
  the signing base, idempotency semantics, and kind/digest validation.
- Crypto: BouncyCastle.Cryptography (managed Ed25519; net10 has none
  built in) - no other behavior changes.

## 1.0.154 to 1.0.156

- 1.0.154: cli-docs re-audit - standard help face rendered from the live
  command tree (SCL's HelpAction is sealed), unified help routing, exit
  codes 0/1/2, README badges + sha256 line, CI agent-face smoke.
- 1.0.155: issue #5 diagram render determinism (seeded Math.random + frozen
  clocks in every mermaid page; A/B triple md5-equal on lan-mac); issue #14
  wave a - `ok` mirror, typed CTA in error meta, the
  `--format/--filter-output/--full-output/--schema` family, envelope-level
  filter, yaml/md renderers.
- 1.0.156: `OFFICECLI_ENVELOPE=strict` preview (wave b enabler): ok-only
  envelope + stderr single-line `{code, message, cta}` on errors.
