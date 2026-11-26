# Server-brokered threshold file sharing

**Status: implemented in the fork, not cryptographically audited.** AI-assisted design and code reviews have
informed this document, which describes the design as implemented. The exact wire format, encodings and
checks are specified in [README.md](README.md) (authoritative); the server is in
`MihuBot/Molly/Sharing/` and the client in molly2's `org.thoughtcrime.securesms.filesharing` package.

## Overview

A shares a small encrypted file or text note (at most 64 KiB) with B. B can recover it only with the
server's cooperation and approval from at least M of N trustees nominated by A (`1 <= M <= N <= 32`), for
example two of four trustees C/D/E/F. Larger content is out of scope: users share it elsewhere and put
links, passwords or instructions into the shared note.

A creates every per-entry cryptographic object and uploads it to the server. Trustees need only their
existing private identity keys: they receive no share and need not know they were nominated until recovery
starts.

Before B starts recovery, the server cannot read the trustee roster. B never needs to communicate with
trustees or receive their identities through the protocol. The server collects their contributions, but
cannot independently decrypt the file: the key it recovers is still sealed to B's identity key.

Each entry has exactly one recovery attempt. Until it starts, A can replace the trustees, threshold and
instructions as a new revision of the same entry; B, the file and its key stay the same.

## Parties

| Symbol | Role |
| --- | --- |
| A | Trustor, file owner, and creator of the grant. |
| B | Intended file recipient and initiator of recovery. |
| C/D/E/F | Trustees; any M of them may approve recovery (two in the examples). |
| S | Remote server (MihuBot), ciphertext custodian, recovery broker, and revocation authority. |

B and S are additional required participants; neither counts toward the trustee threshold.

## Goals and accepted limits

| Goal | Boundary |
| --- | --- |
| S cannot independently learn file contents. | Authorized B or A can disclose them to S. That is accepted. |
| B cannot recover the file without S and a trustee quorum. | Released secrets and plaintext cannot be recalled. |
| S cannot learn the trustee roster before recovery starts. | The roster is padded to 32 slots. B and S colluding can reveal it early. |
| B does not learn trustee identities, count or threshold through the protocol. | S can disclose them after learning them. That is accepted. |
| S and the network do not learn the file's size, name or type. | Content is padded to 64 KiB and its metadata is sealed to B. B learns the exact size, name, type and A's message to B before starting recovery. |
| A can revoke a grant at any time. | Honest S enforces revocation before its final release commit. A released entry can be deleted from S, not recalled from B. Malicious S may retain state and ignore revocation. |
| Trustees are unaware of nomination until contacted. | General client enrollment and possession of an identity key are prerequisites, not per-entry consent. |
| Trustees explicitly approve recovery. | A compromised trustee client can approve without its user's consent. Trustees cannot decline; refusing looks like not responding. |
| A can verify who approved. | Each approval carries the trustee's signature, which A's client checks against the roster it pinned. S can withhold or delay approvals but cannot forge them. |
| A hostile reverse proxy in front of S learns nothing useful. | All share traffic uses the sealed, forward-secret Molly transport with fixed-size or bucketed padding. The proxy still sees IP addresses, timing, request counts and coarse size classes. |

A malicious S may stop recovery, suppress or delay notifications, omit trustees, lie about progress or
request context, or ignore revocation. Trustee contributions it receives can be retained, and a recovered
quorum determines every share. These are accepted risks, not properties that encryption prevents.

## Assumptions

### Identities and keys

- Each Molly device registration (entry) is bound to at most one Signal identity (ACI and identity public
  key) at a time. The device proves possession of the identity private key with `associate`: a MAC keyed by
  an X25519 agreement between its identity key and the server transport key the request is sealed to,
  bound to that exchange. S cannot check that the ACI belongs to the key; peers check that against their
  own stored identities, so S authorizes every action by exact (ACI, key) pairs. A device can switch to a
  different identity at most once per 24 hours, and a wipe unbinds it.
- All participating clients use the same server and satisfy its attested-device eligibility policy. An
  opaque blob is never sufficient authentication by itself.
- A nominates only accepted Signal contacts (system contacts or accepted conversations) and pins their
  current stored identity keys. Contacts with a verified safety number are preferred; unverified ones are
  allowed but highlighted.
- Each entry pins A's ACI and identity key at creation. B and the trustees act on an entry only while A is
  a locally trusted contact whose *current* stored identity key equals the pinned one. There is no option to
  keep trusting a replaced owner key (see "Identity changes").
- Trustee ACIs and keys are pairwise distinct and distinct from A's and B's. A enforces this and S checks it
  again when it learns the roster. Distinct keys/accounts do not prove distinct humans or independent
  compromise domains.
- Trustees ignore requests from an unknown or untrusted A. Their trust decision uses their own local state,
  not an owner key or trust assertion supplied by S. A knowing a contact does not guarantee that the contact
  knows or trusts A.
- Sealing uses libsignal's HPKE to the recipient's Signal identity key. Signal's messaging ratchet is not
  modified.
- A replacement identity key cannot decrypt material sealed to the old key. Old devices or compromised
  clients may nevertheless retain the old private key. A safety-number change is not destruction of that key.

### Cryptographic framing

- All symmetric encryption is XAES-256-GCM with random 24-byte nonces, each under a subkey derived with
  HKDF-SHA512 from its key, purpose label and entry ID, with the same binding as associated data. Distinct
  uses of one key (SKEY's roster and share envelopes) therefore never share a subkey. SHA-512 is used for
  every hash, HMAC and HKDF; the only other hash is inside libsignal's fixed HPKE suite.
- Sealing is libsignal HPKE (`ECPublicKey.seal`), which uses a fresh ephemeral sender key per call, with a
  distinct `info` per purpose and associated data binding the entry, the recipient and A. A's long-term
  signing key authenticates the objects; compromising it does not open previously sealed ones.
- Shamir sharing is bytewise over GF(2^8) with the AES polynomial, indices `1..N`, uniformly random
  coefficients and constant-time arithmetic. A splits; only S reconstructs. It is a small hand-written
  implementation, cross-checked against HashiCorp Vault's and SLIP-39's, not an audited library.
- Every object has a canonical, versioned binary encoding with exact lengths, binding the protocol version,
  entry ID, purpose, and the identities it concerns.
- A signs (XEdDSA, libsignal) the grant descriptor and, per trustee, both the sealed package bytes and the
  package contents. Trustees sign their approvals. Every signature has its own domain,
  `MihuBot.Molly.Share.v1/<purpose>`, and cannot be confused with Signal's other signatures. S stores and
  relays signatures without verifying them; it authenticates A's server-facing objects through the SKEY
  envelopes instead. Clients verify A's signatures against their own trusted key for A, never a key
  supplied by S.
- The descriptor A signs binds the exact lengths and SHA-512 hashes of every B-facing ciphertext, B's and
  A's identities, the entry and its revision, so S cannot relabel one entry as another. It commits to no
  plaintext roster data, so S cannot guess-verify the roster before recovery.
- Clients are never decryption oracles: B only ever sends the exact roster ciphertext opened from the grant
  it verified, and trustees only the exact signed share envelope from their verified package, each only on
  an explicit user action. Every other opened value stays on the device.
- Approvals bind the entry, the attempt's start time, the trustee and the hash of its exact share envelope.

### Storage and metadata

- S stores all uploaded objects and the recovery state, encrypted at rest under per-entry keys derived from
  a server key. This protects against a leak of the database alone, not against a compromise of S.
- A keeps per grant only DATA_KEY sealed to B (never DATA_KEY or the file) and the pinned roster of each
  revision: B's identity, M, and every trustee's identity, index and share-envelope hash. These serve
  updates and the approval audit.
- SKEY is known to A and S, but is never sent to B or trustees.
- Every object has a constant size: the file is padded to 64 KiB, the metadata to 4 KiB, instructions to
  1 KiB, and the roster to 32 trustee slots. Requests and responses are padded to fixed or bucketed sizes,
  so the network does not learn the trustee count either.
- No creation-time liveness or directory lookup reveals nominated contacts to S. A may consequently nominate
  a contact that cannot participate.
- S imposes quotas: at most 32 grants and pending reservations per owner device, 256 received grants per
  identity, of which at most 4 unstarted ones from the same owner device, and 64 listed trustee requests.
  B can delete grants it has not started, and its copy of a released one (see "Retention"). Requests are also rate-limited per IP.

## Cryptographic objects

SKEY, SHARED_SECRET and DATA_KEY are independent random 256-bit keys. There is no symmetric key for B:
everything only B may open is sealed to B's identity key.

| Object | Purpose |
| --- | --- |
| ENTRY | Random 16-byte identifier of the grant, chosen by S when A reserves it. |
| SKEY | Protects the inner roster layer and the trustees' share envelopes; held by A and S. |
| SHARED_SECRET | Split among the trustees; reconstructed only after M approvals; protects B's sealed file key. |
| DATA_KEY | Encrypts the file. |
| ENCRYPTED_FILE | The file, padded to 64 KiB, encrypted under DATA_KEY. |
| FILE_METADATA_SEALED | Kind (file or text note), name, MIME type, exact size and an optional message to B, padded and sealed to B. |
| DATA_KEY_SEALED | DATA_KEY sealed to B. |
| DATA_KEY_SEALED_SHAREDENC | DATA_KEY_SEALED encrypted under SHARED_SECRET. |
| SHARE_T | T's Shamir share with the entry, T's identity, its index, M and N. |
| SHARE_T_SENC | SHARE_T encrypted under SKEY. |
| TRUSTEE_PACKAGE | Entry, A's and T's identities, A's instructions for T, SHARE_T_SENC, and A's signature over all of it. |
| KEY_SHARE_T_SEALED | TRUSTEE_PACKAGE sealed to T, accompanied by A's signature over the sealed bytes. |
| TRUSTEE_MANIFEST | M, N and 32 slots (trustee identity, index, KEY_SHARE_T_SEALED and its signature), unused slots zero. |
| TRUSTEE_MANIFEST_SENC | TRUSTEE_MANIFEST encrypted under SKEY. |
| ROSTER_SEALED | TRUSTEE_MANIFEST_SENC sealed to B. |
| GRANT_DESCRIPTOR | Entry, revision, A's and B's identities, and the lengths and hashes of ROSTER_SEALED, DATA_KEY_SEALED_SHAREDENC, FILE_METADATA_SEALED, ENCRYPTED_FILE and DATA_KEY_SEALED; signed by A. |
| Approval | T's signature over the entry, the attempt's start time, T's identity and the hash of SHARE_T_SENC. |

`Seal`, `Enc` and `Open` below abbreviate the authenticated framing and signatures required above, not
bare unauthenticated operations.

`Seal(RECIPIENT_PUBLIC_KEY, payload)` protects the payload for the recipient, independently of A's
long-term signing key. A initially knows the plaintext; the recipient-only decryption claim assumes A has
erased it and its sealing secrets. Copies of the recipient's private key, including on linked devices, can
also open the payload.

Trustee-visible instructions sit outside the SKEY encryption but inside the trustee's sealed package, and
are signed by A. Instructions may describe the intended recipient in free-form text; **trustee packages do
not contain B's identity**. Cryptographic recipient binding remains in A's B-facing and server-facing
objects.

## Creation flow

1. A asks S for a fresh ENTRY (`share-reserve`), chooses B, the trustees, M, per-trustee instructions and
   an optional message to B, pins their current identities, and generates SKEY, SHARED_SECRET and DATA_KEY.
2. A creates the file-key chain:

   ```text
   ENCRYPTED_FILE            = Enc(DATA_KEY, FILE padded to 64 KiB)
   FILE_METADATA_SEALED      = Seal(B_PUBLIC_KEY, FILE_METADATA)
   DATA_KEY_SEALED           = Seal(B_PUBLIC_KEY, DATA_KEY)
   DATA_KEY_SEALED_SHAREDENC = Enc(SHARED_SECRET, DATA_KEY_SEALED)
   ```

3. A splits SHARED_SECRET into N shares with threshold M. For each trustee T:

   ```text
   SHARE_T_SENC = Enc(SKEY, share + ENTRY + T's ACI/key + index + M + N)

   TRUSTEE_PACKAGE = ENTRY + A's ACI/key + T's ACI/key + instructions
                   + SHARE_T_SENC + A's "trustee-package" signature

   KEY_SHARE_T_SEALED = Seal(T_PUBLIC_KEY, TRUSTEE_PACKAGE)
   A's "trustee-sealed" signature over ENTRY, A, T and the hash of KEY_SHARE_T_SEALED
   ```

   Trustees will return the SKEY-encrypted envelope, not a plaintext share. No owner signature on the
   plaintext share is needed: honest S validates its SKEY envelope. T cannot inspect the plaintext share
   metadata; S validates it when T contributes.

4. A builds TRUSTEE_MANIFEST, padded to 32 slots, and encrypts it twice:

   ```text
   TRUSTEE_MANIFEST_SENC = Enc(SKEY, TRUSTEE_MANIFEST)
   ROSTER_SEALED         = Seal(B_PUBLIC_KEY, TRUSTEE_MANIFEST_SENC)
   ```

   The roster is not signed: S authenticates it through the SKEY envelope, B cannot read it, and trustees
   never see it.

5. A signs GRANT_DESCRIPTOR over all B-facing objects.
6. A durably stores the pinned roster and DATA_KEY_SEALED, then uploads in one `share-create`: the
   descriptor and its signature, ROSTER_SEALED, DATA_KEY_SEALED_SHAREDENC, FILE_METADATA_SEALED,
   ENCRYPTED_FILE and SKEY. An identical retry is idempotent. A then erases the file, DATA_KEY, SKEY,
   SHARED_SECRET and the shares.

S checks that the caller is A, the sizes, and the descriptor hashes, and stores the entry. It stores SKEY
but cannot open ROSTER_SEALED, which is sealed to B, and cannot read any share, because each SKEY envelope
is additionally sealed to its trustee. No trustee is contacted during creation; B's client lists the new
grant on its next sync.

### Updating the trustees

While recovery hasn't started, A can replace the roster with `share-update`: a new revision with a fresh
SKEY and SHARED_SECRET split over the new trustees and threshold, A's kept DATA_KEY_SEALED re-encrypted
under the new SHARED_SECRET, new trustee packages and a newly signed descriptor. S checks that B, the file,
its metadata and DATA_KEY_SEALED are unchanged, replaces the roster and SKEY atomically and erases the old
SKEY, so an old roster can no longer start recovery. Trustee packages are only released after a start, so
no share of a replaced roster was ever handed out.

Only A's creating device, still bound to A's pinned identity, can update. Its client updates automatically
when a trustee's identity key changed and the new safety number is verified (otherwise it keeps the pinned
key and flags the grant to A), and when A edits the trustees, threshold or instructions. Once recovery
starts, the roster is fixed.

## Recovery flow

### 1. B discovers and starts a grant

S lists the grants addressed to B's current identity, with A's identity and a generic state. On request it
returns the signed descriptor, ROSTER_SEALED and FILE_METADATA_SEALED, before and after recovery starts.

B first verifies that A is a trusted contact with the pinned key, A's grant signature, that the descriptor
names this entry and B's own identity, and that every returned object matches the descriptor. Only then
does it open the metadata to show what was shared and, on explicit start:

```text
TRUSTEE_MANIFEST_SENC = Open(B_PRIVATE_KEY, ROSTER_SEALED)
```

B sends exactly this ciphertext in the start request. B cannot decrypt it, and it never returns any other
opened value to S, so S cannot use B as a cross-stage decryption oracle.

S decrypts it with SKEY and validates the roster, policy, distinctness and padding. Invalid requests are
rejected without committing a started state, counting approvals or notifying anyone; they do not consume
the grant. A rejected start may also mean A updated the trustees meanwhile, so B fetches the newest
revision and retries once.

After successful validation, S atomically moves the entry from active to started, unless a revocation
committed first, and records the start time. **B's submission lets S learn the trustee identities, count
and threshold while validating it; that knowledge cannot be recalled even if the start then loses to a
concurrent revocation.**

There is exactly one attempt per entry. It does not expire, B cannot cancel it, and repeating the start
only confirms it; A ends it by deleting the entry. S raises the update hints of A, B and every trustee, so
their clients sync: A is alerted that recovery started, and trustees see an approval request. Notifications
do not themselves authorize release.

### 2. Trustees review and approve

Each trustee's sync lists its own sealed package with the entry, A's identity and the attempt's start time,
without other trustees' identities, until it approves.

The client first rejects unknown or untrusted owners and owners whose current key differs from the listed
one. It verifies A's signature over the exact sealed bytes, then opens the package locally:

```text
TRUSTEE_PACKAGE = Open(T_PRIVATE_KEY, KEY_SHARE_T_SEALED)
```

It checks the package's version, entry, owner and its own identity, and A's signature over the package,
before showing A's instructions, as A's words and apart from anything S reports. Only after explicit user
confirmation does T send the exact SHARE_T_SENC field from the package, with its approval signature over
the entry, the attempt's start time, its identity and the envelope's hash. T never receives SKEY and need
not learn its share. T can dismiss a request locally; S is not told.

Trustee approval authorizes contribution to A's entry according to A's instructions. It does not
independently authenticate B's initiation: trustees are not given B's identity or a B-signed start
request, and the start is S's account. S alone cannot open the roster before B starts, so it cannot
contact trustees with their packages earlier. This is an accepted distinction between trusting A's grant
and trusting S's account of the current request.

S decrypts SHARE_T_SENC with SKEY and checks that the share names this entry, that the caller's bound
identity is both the share's trustee and the roster slot's, the index, M and N, and the attempt's start
time. Each trustee counts at most once; repeating an accepted approval returns its result but is never an
extra vote. A compromised trustee can still issue a fresh, valid approval without its user's consent.

S stores the share and the approval signature and raises A's update hint. A's client verifies every relayed
signature against the roster it pinned for that revision and shows who approved. B only ever sees the
generic states available, started and released: no approval counts, threshold or trustee count. Traffic
timing remains observable.

### 3. S reconstructs and validates

The approval that reaches M triggers, in the same transaction:

```text
SHARED_SECRET   = Shamir_Combine(M shares)
DATA_KEY_SEALED = Dec(SHARED_SECRET, DATA_KEY_SEALED_SHAREDENC)
SHA-512(DATA_KEY_SEALED) must equal the descriptor's hash
```

Successful authenticated decryption validates reconstruction, and the descriptor check ensures B receives
exactly what A committed to. On failure the whole transaction rolls back: that approval is not counted and
nothing is released. S cannot open DATA_KEY_SEALED.

S knows SKEY and could manufacture share envelopes if malicious, but cannot manufacture the correct shares
or otherwise recover the original SHARED_SECRET; invented contributions fail the authenticated decryption.

After quorum, S can derive every original share by reconstructing the sharing polynomial, including shares
from trustees that never approved. For 2-of-4, two points determine the degree-one polynomial. The quorum is
a barrier to initial reconstruction, not continued secrecy of the other shares.

### 4. S commits release; B decrypts

In that same transaction S moves the entry from started to released, unless a revocation committed first,
stores DATA_KEY_SEALED and erases SKEY and every trustee share. It keeps the roster and the approval
signatures for A's audit, and raises the update hints of A, B and the trustees that have not approved.

B fetches DATA_KEY_SEALED and ENCRYPTED_FILE, checks them against the signed descriptor of the revision it
started, and decrypts:

```text
DATA_KEY = Open(B_PRIVATE_KEY, DATA_KEY_SEALED)
FILE     = Dec(DATA_KEY, ENCRYPTED_FILE)
```

It also checks that the file's length matches the metadata, then saves the content where the user chooses.
Any device holding B's identity key, such as a linked device, can fetch it; the policy identifies an account
and key, not one phone. S keeps a released entry for 30 days so B can fetch it again.

A's client flags a release without M valid approval signatures as not independently attested. That is not
proof that S cheated: S does not verify signatures, so a trustee could have submitted a bad one.

## Revocation, retention and identity changes

A revokes by deleting the entry, in any state, from the device that created it. An honest S erases the
encrypted file, SKEY, roster, shares, DATA_KEY_SEALED and approvals; deleted rows are overwritten by the
database (`secure_delete`).

Start, release and deletion are conditional state transitions in serialized transactions, so concurrent
revocation and release have a definite order. Revocation that commits first prevents release. Once release
commits, A cannot rely on recalling the output; deleting a released entry only removes S's copy.

This is a server-enforced guarantee, not cryptographic deletion:

- Malicious S can retain state and ignore revocation.
- A completed release cannot be reversed.
- B can retain recovered keys, ciphertext, and plaintext.

### Retention

- Released entries are deleted 30 days after release.
- Active and started grants are deleted once their owner device has made no share request for 14 days
  (clients make them while unlocked); a started recovery also gets 14 days from its start. A has to keep
  using the app for its grants to stay alive.
- Grants of a deleted or wiped device are deleted. Unused reservations are dropped after an hour.
- B can delete a grant it has not started. B's client does so automatically for unstarted grants whose listed
  owner can never become trusted (unknown, blocked, or itself) or whose owner key still does not match B's
  stored one 24 hours after creation, so that others cannot fill B's received quota.
- Once released, B can also delete its copy: S erases the file and everything only B uses, while A keeps the
  released record and its approvals.
- Honest S erases trustee shares at release or deletion. Malicious S, backups or stolen snapshots may retain
  them, and a retained quorum, SHARED_SECRET or DATA_KEY_SEALED lets B and S complete recovery without
  further trustee action, even after A deleted the entry.

### Identity changes

Grants are owned by the device (Molly entry) that created them: only it lists, updates and deletes them,
including after its identity changed. A device whose app data was cleared registers a new entry and cannot
manage its old grants, which then expire through the inactivity rule.

- **A's identity key changes:** B and the trustees no longer act on A's existing entries, since the pinned
  owner key is no longer A's current one; accepting A's new key does not restore trust in the old one. A's
  device can still delete them, but not update them, and A must create new grants.
- **B's identity changes:** S authorizes by exact (ACI, key) pairs, so grants to B's former identity are no
  longer accessible to B. A has to share again.
- **A trustee's identity changes:** before recovery starts, A's client updates the roster once the new
  safety number is verified, and otherwise flags the grant. After the start, the trustee can no longer
  approve with the new identity; A can delete and re-share if a quorum is out of reach.

An old key may survive on linked devices or in stolen state. Dropping out after a key change therefore
depends on clients' trust checks and honest S, not on the safety-number change alone.

## Who learns what?

The table describes the intended flow with honest parties. A knows all creation secrets from the outset.
Instructions and the message to B can deliberately reveal additional information.

| Information | A | B before start | B after start, before release | S before start | S after start | Individual trustee when contacted |
| --- | --- | --- | --- | --- | --- | --- |
| ENTRY / A's identity | Yes | Yes when listed | Yes | Yes | Yes | Yes |
| B's identity | Yes | Own identity | Own identity | Yes | Yes | Not required; instructions may describe B |
| File name, type, size, message to B | Yes | Yes | Yes | No (constant sizes) | No | No |
| File plaintext / DATA_KEY | At creation | No | No | No | No | No |
| SKEY | At creation | No | No | Yes | Until release | No |
| Trustee identities/count/threshold | Yes | No | No | No (padded roster) | Yes | Own nomination; instructions may reveal more |
| Trustee instructions | Yes | No | No | No | No | Its own |
| Plaintext Shamir shares | At creation | No | No | No | Received contributions; all derivable after quorum, while retained | No; returns its encrypted envelope |
| SHARED_SECRET | At creation | No | No | No | After quorum | No |
| DATA_KEY_SEALED | Kept | Not before release | Not before release | Only inside its SHARED_SECRET envelope | After quorum | No |
| Recovery started / approvals | Notified; verifies signed approvals | Own action | Generic states only | Not yet applicable | Yes | Its own request, subject to S's honesty |

After release, B learns DATA_KEY and the file plaintext. B still does not learn SKEY, SHARED_SECRET,
plaintext shares, or trustee identities.

S observes which devices and identities act, timing, and entry relationships (owner device, recipient, and
the trustees once started). Ciphertext sizes are constant and reveal nothing about the content. This design
is not protection against traffic analysis.

## Required cooperation

This table describes a fresh entry before enough correct contributions or reconstructed secrets have
accumulated, with no previous release or disclosure by A, and a threshold of two. "Compromised" includes
access to relevant keys and available state.

| Cooperating parties | Recover original file? | Other consequences |
| --- | --- | --- |
| B alone | No | Can open its roster layer and start recovery. |
| S alone | No | Can block, retain state, or lie; cannot open ROSTER_SEALED. |
| B + S | No | Can expose the roster before an ordinary start. |
| B + S + one trustee | No | Can obtain one correct share, below threshold. |
| S + two trustees, without B | No | Cannot obtain trustee packages until B opens ROSTER_SEALED. Once packages are available, can reconstruct SHARED_SECRET, but DATA_KEY_SEALED still requires B's private key. |
| B + two trustees, without S or SKEY | No | Cannot extract packages from the SKEY-encrypted roster. Even if delivered, trustee unsealing yields SKEY-encrypted envelopes, not plaintext shares. |
| B + S + two trustees | Yes | Meets all key-possession requirements; compromised trustees need not obtain human approval. |
| B + S retaining a quorum, SHARED_SECRET or DATA_KEY_SEALED | Yes | No further trustee action is needed, even after revocation. |
| A's device after creation, with or without S | No | A keeps only DATA_KEY_SEALED and pinned rosters; B's private key is still required. |
| A, while retaining the file or creation secrets | Yes | A is the creator, not restricted by the recovery threshold. |

The SKEY-encrypted share envelopes provide the server-secret dependency: even trustees with their private
keys do not independently obtain plaintext shares. Sealing DATA_KEY to B provides the recipient dependency:
a quorum with S yields only DATA_KEY_SEALED.

## Worst cases by compromised party

| Compromise | Worst case and limits |
| --- | --- |
| A during creation, or while retaining plaintext/keys | File disclosure and arbitrary grants. An attacker can also revoke entries or create deceptive owner-authenticated requests. |
| A's device after creation | Can delete A's grants, create deceptive new ones, and replace the trustees of grants not yet started, for example with colluding ones. It still needs B's private key, S and a quorum to recover a file. Participants stop trusting A's old key once A's identity changes. |
| B | Attacker can learn the file's name, size and A's message, and start recovery, causing S to contact the trustees. No plaintext without S and a quorum. After legitimate release, B can retain or disclose everything it recovered. |
| One trustee | Attacker can approve without consent or withhold participation. That trustee counts at most once, and its signed approval is attributable to it in A's audit. One contribution alone is insufficient. |
| Two or more trustees | Human-consent protection is lost for a quorum. The attacker still needs S's share-envelope decryption and B's private key to recover the file. |
| S before recovery | All server metadata, SKEY and the encrypted objects are exposed; availability and revocation promises can be broken. S alone still cannot open the roster, the shares or the file. A B-submitted roster can be read even if its start then loses to a revocation. |
| S after recovery starts, before quorum | The roster and any received shares are additionally exposed. S can suppress or delay notifications, fabricate request context, omit honest trustees, and withhold approvals from A. It cannot invent missing correct shares or trustee signatures that A's client accepts. |
| S after quorum, before release | SHARED_SECRET, the complete sharing polynomial and DATA_KEY_SEALED are exposed while retained. Malicious S can release despite revocation with no further trustee approval, but B's private key is still needed to open DATA_KEY. |
| S after release | S can retain its intermediate secrets and infer that it authorized release. It still does not obtain DATA_KEY; B's subsequent disclosure is an accepted risk. |
| S's database alone | Share fields are encrypted at rest under a server key that is not stored in the database. |
| A hostile reverse proxy | Sees only sealed, padded, forward-secret exchanges: IP addresses, timing, request counts and coarse size classes per action. |

Private identity keys may be present on multiple linked devices. Such devices represent copies of one
party's authority, not extra trustees or extra votes.

## Open items

- S verifies possession of an identity key, not that the ACI belongs to it; peers check that against their
  own stored identities. Freshness of registrations after account recovery or with retained old devices,
  and administrative recovery of grants for a device whose app data was cleared, are not addressed beyond
  the inactivity expiry.
- S has no push channel: clients learn of changes through update hints in their periodic pings and sync
  only while unlocked, so S (or the network) can delay notifications.
- S cannot verify XEdDSA signatures with the platform's built-in cryptography, so it cannot reject a bad
  approval signature; A's client flags releases that are not independently attested instead.
- The protocol, its encodings and the Shamir implementation have not had an independent cryptographic
  audit, which is needed before the document can be treated as a deployable protocol specification.
