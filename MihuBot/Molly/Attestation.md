# Molly Android attestation

Logins release the server secret only to a device that proves, with Android hardware key attestation,
that it is the device the entry was enrolled from: same app-generated hardware keys, same verified
boot identity, bootloader still locked, and no app/OS/patch downgrade.
Everything below travels inside the encrypted transport described in [README.md](README.md).

## Trust model

- **Trust on first use.** The first attested login (enrollment) accepts any hardware-backed (TEE or
  StrongBox) key on a locked device whose verified boot state is `Verified` (e.g. stock Android) or
  `SelfSigned` (e.g. GrapheneOS), and pins its verified boot key and state. There is no OS allowlist,
  and enrollment can't prove the device was clean at that point.
- **Strict mode** (preferred) pins a persistent `PURPOSE_ATTEST_KEY` key. Each login's fresh key must
  be attested *by that key*, which ties the evidence cryptographically to the enrolled hardware.
  Requires Android 12+ and `FEATURE_KEYSTORE_APP_ATTEST_KEY`, which not every device has.
- **Fallback mode** is for devices without app attest keys. A pinned hardware signing key signs a
  transcript containing fresh manufacturer-rooted attestation. This proves possession of the pinned
  key, but the fresh evidence itself isn't bound to the enrolled hardware: it only has to match the
  pins, and the verified boot key is shared by every unit of a model, so whoever holds the signing
  key could present fresh attestation from another unit. It's shown as weaker.
- Pinned: mode, persistent public keys, security level(s), verified boot key and state, package name
  and signing certificate digest. High-water marks: app `versionCode` (as attested, unrelated to the
  version string pings report), OS version, OS/vendor/boot patch levels. Forward updates are fine; changes to pins or any downgrade are rejected.
- Pins are permanent; there is no recovery or re-pairing. Clearing app data, reinstalling or a
  factory reset also loses the device-bound Keystore HMAC key that the key hash is derived from, so
  even the same passphrase then produces a new key hash, registering and enrolling a new entry. The
  old entry is orphaned with its pins. The mode is pinned too: a fallback enrollment can't later
  become strict. If an enrolled entry's login fails attestation (anything but a temporary or retryable
  failure, or a certificate that isn't valid at the server's current time), the entry is locked until
  an admin unlocks it, and the operator is notified.
- Attestation describes the booted software and hardware-enforced state. It doesn't prove the
  absence of runtime exploits. The app identity (package and signer) is reported by the OS rather than
  the secure hardware, so it's only as trustworthy as the verified, locked boot it comes with.
  Background requests (`ping`, `alert`, `associate`) are not attested, but only accept the newest `id`
  the device has used (see below).

## Freshness

There is no separate challenge request: the transport key a login is sealed to (see
[README.md](README.md)) provides the freshness. The server generates a new one every 30 seconds, and
none can be predicted before the server publishes it. Rotation is checked every 10 seconds, so the key
discovery and key updates describe was normally generated at most 40 seconds earlier. An
attested login must be sealed to a key the server generated at most 2 minutes earlier, by its own
clock, or it gets `invalid`. Keys stay usable
for other requests for longer (a key's `expiresAt` is 30 minutes after it was generated), so a cached
key is usually too old: when the unlock screen is shown, do authenticated discovery, and repeat it only
if the key would be more than 2 minutes old when the login is sent, allowing for the KDF, key
generation and network latency. Measure a key's age from when it was received (plus up to 40
seconds), not from `expiresAt`, so client clock skew doesn't matter.

The fresh per-login key is attested with a challenge derived from the exchange: SHA-512 over these
fields, each encoded as `BE32(length) || bytes`:

1. `UTF8("MihuBot.Molly.AttestationChallenge")`
2. Version byte `0x01`
3. The exchange's 16-byte unmasked recipient key ID (see the transcript below)
4. The exchange's 32-byte client ephemeral X25519 public key
5. The request's decoded 16-byte JSON `nonce` (not the AEAD nonce)

So the attestation can't predate the transport key, and it's unique to the request: the transport
only accepts a request nonce once, and each request has a new ephemeral key. Generate the nonce along
with the ephemeral key, before the fresh key, and send that same nonce in the login request.

Every login uses a key set known before the passphrase is typed (the committed one, or the one just
generated for a setup or passphrase change; see the key lifecycle below). So once the transport key
is known, generate the request's ephemeral key, nonce and fresh key while the KDF runs.

## Login

`login` data: `{"keyHash": "...", "attestation": {...}}`, where `attestation` contains:

| Field | Meaning |
| --- | --- |
| `version` | `1` |
| `mode` | `strict` or `fallback` |
| `freshChain` | Fresh per-login key's certificates, base64 DER, leaf first |
| `signingKeyChain` | Persistent signing key's certificates |
| `attestKeyChain` | Persistent attest key's certificates (strict mode only) |
| `signature` | Base64 DER ECDSA P-256/SHA-256 signature of the transcript by the persistent signing key (StrongBox only has to support SHA-256) |

Chains hold at most 8 certificates of at most 4096 bytes each. The request body limit is 32 KiB.

| Mode | `freshChain` | `signingKeyChain` | `attestKeyChain` |
| --- | --- | --- | --- |
| strict | Only the leaf (issued by the attest key) | Only the leaf (issued by the attest key) | Full chain to a Google root |
| fallback | Full chain to a Google root | Full chain to a Google root | Omitted |

Always send `signingKeyChain`/`attestKeyChain`. They are needed whenever the key hash has nothing
pinned yet (a new or changed passphrase, a typo, or a lost enrollment response), which the client
can't always know. Enrolling validates them fully, so a key set whose short-lived (RKP) certificates
have expired can no longer enroll a new key hash. Once keys are pinned, the server only checks that
the re-sent chains are for them, not whether their certificates are still valid.

### Transcript

The signing key signs these concatenated fields, each encoded as `BE32(length) || bytes`:

1. `UTF8("MihuBot.Molly.Attestation")`
2. Version byte `0x01`
3. `UTF8(mode)`: `strict` or `fallback`
4. The exchange's 16-byte unmasked recipient key ID: the first 16 bytes of SHA-512 of the transport
   public key the login request is sealed to (see [README.md](README.md))
5. The exchange's 32-byte client ephemeral X25519 public key
6. The decoded `keyHash` bytes
7. SHA-512 of the signing key's DER SubjectPublicKeyInfo
8. SHA-512 of the attest key's DER SubjectPublicKeyInfo, or empty in fallback mode
9. Chain hash of `freshChain`
10. Chain hash of `signingKeyChain` as sent
11. Chain hash of `attestKeyChain` as sent

Chain hash = `SHA-512(BE32(count) || (BE32(length) || der)...)` over the exact DER bytes sent. An
omitted chain is hashed as a chain of zero certificates (`SHA-512(00 00 00 00)`), so fields 9-11 are
always 64 bytes; only field 8 can be empty.

Fields 4 and 5 bind the proof to the transport exchange it is sent in, so it can't be replayed inside
an exchange whose response someone else can decrypt. The transport has to expose the ephemeral key and
recipient key ID before sealing: generate the request's ephemeral key and nonce first, then the fresh
key, sign, then seal the request with that same ephemeral key and nonce. A retry needs a new
ephemeral key, so it also needs a new fresh key and signature: retry at the login level, never by
re-sealing an already signed request in the transport (such a request gets `invalid`).

Test vector (arbitrary inputs): mode `strict`, recipient key ID `11` × 16, ephemeral key `22` × 32,
request nonce `00..0F`, key hash `33` × 32, signing key SPKI `44 44`, attest key SPKI `55`, fresh
chain `[66 66 66]`, empty signing key chain, attest key chain `[77], [88 88]`. The attestation challenge is
`CFBD28E629A395A7F871D59FD09A584662543AC1DFB25208B5B65611C5623CD1B5D93817A73BA1FF3636442AD4056101EF2193036B21DAAA4B6A4CC68CD20A2E`.
The transcript is 476 bytes with SHA-512
`51CC23F02A83400EF21A1E8984E573DD091AF68DF35ED531CA7721381AEC4DC09D0C7EE39B1D651562901F1793D252293D7C2C526B52AD7CC0518BF209A34891`.

### Results

| Status | Meaning | Client action |
| --- | --- | --- |
| `ok` | `serverHmac` and `id` as before | See below for when to keep them |
| `command` | Lock/wipe, delivered without attestation. Also the answer to a login of an enrolled entry that failed attestation, which locks the entry | As before |
| `invalid` | Anything else: a malformed request or missing attestation, a transport key older than 2 minutes, a lost race, an enrollment whose evidence doesn't verify, or the server can't verify right now | Retry once in a new exchange (with a recent transport key, a new ephemeral key, fresh key and signature); if that is `invalid` too, the login failed (see the key lifecycle below). Never replace keys |

The server doesn't tell clients why a login was rejected.
None of these is a wipe command or a reason to clear an existing background session.

A successful login's `id` supersedes the `id`s issued by the entry's earlier logins once it is first
used: from then on, `ping`, `alert` and `associate` requests with an older `id` get `invalid`. Until
then the previous `id` keeps working, so a login whose response is lost, or that the client doesn't
keep, doesn't cost the device its background session. Replace the stored `id` once the login is kept,
and persist it before using it.

Each unlock usually takes two requests (discovery, login), which count towards the per-IP rate limit:
a burst of 10, then one per minute. That is only about five unlocks (or mistyped passphrases) in quick
succession before requests are throttled, so budget retries accordingly.

### Persistent key lifecycle

The app's persistent keys (its key set) belong to the Keystore HMAC key that the key hash is derived
with: name them after its alias (molly2's `keystore_alias`, a random UUID), e.g. `<alias>.attest`
and `<alias>.sign`, and store their mode and security level in the same preferences commit as the
alias. They are created, committed and deleted together with the HMAC key, so the committed alias
always names both the HMAC key and the keys its entry is pinned to.

Initial setup and passphrase changes (or removing the passphrase, or anything else that creates a new
HMAC key) generate a new alias, HMAC key and key set, so each attempt enrolls a new key hash with keys
generated for it. If the attempt fails for any reason, including `invalid` or a timeout, abort it:
delete the new HMAC key and key set, and commit nothing. Whatever the attempt may have enrolled on the
server is orphaned and harmless, and the next attempt starts over with a new alias. Never retry an
enrollment with different keys for the same key hash. A passphrase change takes two logins, each in
its own exchange: one with the old passphrase and the committed keys to unlock, then the enrollment
of the new key hash with the new keys.

Unlocks use the committed key set; never delete or replace it, other than as part of a committed
passphrase change. The server can't tell a mistyped passphrase from a new one, so an unknown key hash
is enrolled with these keys and returns `ok`. Only treat a login as successful, and only keep its
`id`, once the returned secret has unlocked the local data; otherwise report an incorrect passphrase
(the typo's entry is harmless). `invalid` (after its retry) means a temporary server problem or a
malformed request; either way that unlock failed. A key hash is only ever attested with the keys of
its own alias, so a correctly behaving client only gets `command` (lock) from a login once the device
no longer matches its pins. Never have two logins with the same keys in flight at once.

Keys under any other alias were left behind by an interrupted attempt; delete them at startup.

A client registered before attestation generates a key set under its existing alias and enrolls its
existing entry with it. As its key hash can't change, it keeps those keys and retries later if
enrollment fails. There is no client-side way out if enrollment never succeeds (e.g. the device fails
the policy, or the key set's short-lived certificates expire first): a passphrase change needs the
existing entry's secret, so it can't enroll a new entry either. Its stored `id` predates the current
format and gets `invalid`, so background requests only work again once a login succeeds.

## Server policy

- Chains must end in a pinned Google attestation root key (RSA, and the 2025 ECDSA P-384 generation),
  and no other certificate in the chain may carry a root key. Only the leaf of a manufacturer chain may
  carry the attestation extension.
- Chains are verified from the root down, and issuer keys must be RSA 2048-4096 bits (with an exponent
  of at most 32 bits) or EC P-256/P-384/P-521. Signatures are checked over the certificate bytes
  exactly as sent.
- Issuer names must chain. The validity of issuing certificates is checked for every manufacturer
  chain verified at enrollment and for fallback logins (5 minutes of clock skew). Like Google's own
  verifier, expired certificates of factory chains (the intermediate below the RSA root names a serial
  number, and no certificate has the RKP provisioning info extension) are still trusted unless revoked.
  The validity of device-generated certificates is ignored: it's set on the device, often clamped to
  an expired factory certificate, and freshness comes from the exchange-derived challenge. Strict
  logins don't re-validate the enrollment chain, so short-lived RKP enrollment certificates don't
  break the pairing.
- Fresh key: attestation version ≥ 3 (Keymaster 4), TEE or StrongBox for both attestation and KeyMint,
  generated origin, locked bootloader, `Verified`/`SelfSigned` with a non-zero boot key, OS version and
  OS patch level present, and exactly one package with a valid Android package name and a single
  SHA-256 signing digest, allowed by `Molly:AttestationApps` if that's configured. A vendor
  or boot patch level of `0` counts as not reported, but a patch level that was reported once can't
  disappear later: that is treated as a rollback, and such logins are rejected for good.
  Fields the policy doesn't use are ignored, however they're encoded.
- Signing key: EC P-256, sign (and optionally verify) purposes only, generated. Attest key: EC P-256,
  only the attest-key purpose, generated. All keys must have the same security level.
- Revocations come from the [official status list](https://android.googleapis.com/attestation/status),
  refreshed hourly and cached with its fetch time. Without a list nothing verifies. After 24 hours,
  enrollment and fallback logins are refused, while enrolled strict devices can still log in. Known
  revocations of current or enrolled issuing certificates are always enforced, and never forgotten,
  even once Google drops an expired certificate from the list, so a lifted suspension stays in effect.
  Serials match in hex (as documented) or decimal (as much of the live list is spelled). Unrecognized
  list entries are skipped.

## Android implementation notes

Use the `AndroidKeyStore` provider. Keep the keys and the preferences naming them outside backups.
Everything has to work before encrypted storage is unlocked. All keys use `KEY_ALGORITHM_EC` with
`ECGenParameterSpec("secp256r1")` and `DIGEST_SHA256`, and the same security level, chosen when the
key set is generated and never changed: pass `setIsStrongBoxBacked(true)` for every key, including
each per-login key, if StrongBox was chosen.
Give the persistent keys a non-empty attestation challenge (any value up to 128 bytes, e.g. the
current exchange's challenge); some implementations omit the attestation for an empty one. App attest
keys (`setAttestKeyAlias`) need API 31+.

Pick the mode and security level whenever a key set is generated, by trying the options in order until
key generation succeeds. Catch `GeneralSecurityException` (e.g. `InvalidAlgorithmParameterException`,
which may wrap a `StrongBoxUnavailableException`, or `KeyStoreException`) and `ProviderException`, and
delete any key a failed attempt did create (e.g. a StrongBox attest key whose child key failed) before
the next one:

1. Strict with StrongBox (`setIsStrongBoxBacked(true)`, needs `FEATURE_STRONGBOX_KEYSTORE`)
2. Strict with TEE
3. Fallback with StrongBox
4. Fallback with TEE

`FEATURE_KEYSTORE_APP_ATTEST_KEY` describes the TEE; a StrongBox may still lack attest key support,
which is why generating a test child key under a StrongBox attest key is part of the probe. Strict
mode in the TEE is preferable to fallback mode in StrongBox.

Strict mode:

1. Attest key: `PURPOSE_ATTEST_KEY`, `secp256r1`, `DIGEST_SHA256`, with an attestation challenge.
   Send its full `getCertificateChain` as `attestKeyChain`.
2. Signing key: `PURPOSE_SIGN`, `secp256r1`, `DIGEST_SHA256`, `setAttestKeyAlias(attest key)`, with
   an attestation challenge. Send element 0 of its chain as `signingKeyChain`.
3. Per login: a new `PURPOSE_SIGN` key with `setAttestationChallenge(exchange challenge)` and
   `setAttestKeyAlias(attest key)`. Send element 0 of its chain as `freshChain`, then delete the key.

Fallback mode: the same, without the attest key and without `setAttestKeyAlias`, sending full chains.

Sign the transcript with `Signature.getInstance("SHA256withECDSA")` using the signing key.
Allow for discovery and key generation within the login time budget, keep the existing
proxy/TLS behavior, and don't fall back to direct connections.
