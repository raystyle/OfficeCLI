# Changelog

 Versions prior to this file live in
 [GitHub Releases](https://github.com/raystyle/OfficeCLI/releases) (auto-generated
 notes per tag).

## 1.0.157

- **issue face switched to the repo ledger** (REQ-063): `officecli issue
  new/list/show/close` now talks to ledger.ohmygh.com (append-only issue +
  artifact streams) instead of the retired issues.ohmygh.com tracker. New-unit
  shape: `issue new "<title>" --kind bug|improvement --acceptance "<criteria>"`;
  closing requires a `result` event citing a registered digest
  (`issue close <n> --digest sha256:...`), then `status=done`.
- **artifact command family** (new): `officecli artifact publish/attest/promote/
  list` registers shared-library products (experience, lesson, research,
  prototype, sbom, ...) by content digest, with dev/prod attestation and
  promote/demote/supersede chains.
- Writes are Ed25519-signed (five-header contract, idempotency keys, per-key
  quota 50/UTC-day); the public key ships in the CLI, the private key is read
  from env / local keyfile only. `__selftest__` now unit-checks the signing
  base, idempotency semantics, and kind/digest validation.
- Crypto: BouncyCastle.Cryptography (managed Ed25519; net10 has none built in)
  - no other behavior changes.
