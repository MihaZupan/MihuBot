# Molly provisioning

The app isn't built with a server address or key. A **setup code** tells a new device where the server
is and which key to pin, and carries a single-use **registration token** without which the server
doesn't register new entries.

## Setup codes

A setup code is a URI, shown as a QR code or entered by hand field by field:

```text
molly2://setup?url=<percent-encoded API URL>&key=<discovery key>&token=<registration token>
```

| Field | Meaning |
| --- | --- |
| `url` | Absolute URL of the single Molly endpoint, e.g. `https://mihubot.xyz/api/molly` |
| `key` | The static X25519 discovery public key to pin ([README.md](README.md)), 32 bytes as unpadded base64url |
| `token` | The registration token: opaque, unpadded base64url |

The app checks the URL and key with an authenticated discovery, then stores them and the token, and deletes them on a wipe. Once a login with them has unlocked the local data, it doesn't let them be replaced while it has a session. It deletes the token once a login
is kept ([Attestation.md](Attestation.md)): only the first enrollment uses it. A device that is
already enrolled, but had no stored server configuration, can use any setup code; its token is
ignored and stays unused.

Setup codes are created on the Molly dashboard (by anyone who can open it), or by an enrolled device
for another one (`registration-token` below).

## Registration tokens

A token is `base64url(nonce (24) || XAES-256-GCM(id (16) || issuedAt ticks (8)) || tag (16))` under a
key derived from the database key. The server stores nothing when issuing one. It can be redeemed for
24 hours by one `login` registering a new entry; the ids of redeemed tokens are kept in memory until
they expire. So after a server restart, a token redeemed within its lifetime could be redeemed again.

## `registration-token`

Request data: `{"id": "<session>"}`, with the usual session rules (superseded `id`s get `invalid`,
pending lock/wipe commands are returned instead).

A successful response's data contains `token` and `expiresAt` (Unix seconds). Each device may request
10 tokens per 24 hours, and all devices together 100 (counted in memory, so a restart resets them); beyond that the
status is still `ok`, but both fields are omitted. The same applies while the "Molly device setup codes" switch on the
admin page is paused. The dashboard isn't limited and can always create setup codes.
