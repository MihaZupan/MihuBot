# Molly transport

All exchanges use `POST /api/molly`, with `application/octet-stream` bodies, over HTTPS.

Responses apply a best-effort timing floor randomly selected per request from 10 ms (inclusive) to 20 ms (exclusive) after rate limiting and reading the request body, including decryption failures and subsequent server errors.
Rate limiting and body reading do not count toward the floor; rejections or errors during those steps are not delayed.
Processing time counts toward this floor; slower requests incur no additional delay, and disconnecting does not cancel the wait.
This reduces timing differences for fast paths, but does not make request processing constant-time.

## Discovering a transport key

The client pins the server's static X25519 public key, which is used **only for discovery**.

Seal `{"action":"transport-key","timestamp":<Unix seconds>,"nonce":"<base64 of 16 random bytes>","data":null}` to the pinned key using the envelope below.
Do not include device identifiers, credentials or other application data in discovery.

Decrypt and authenticate the response with the exchange's response key.
A successful response has `status: "ok"` and a `data` object containing:

| Field | Encoding / meaning |
| --- | --- |
| `publicKey` | Standard padded base64 of the 32-byte X25519 public key |
| `expiresAt` | Unix seconds; stop using this key before this time |
| `discoveryPublicKey` | Standard padded base64 of the latest static 32-byte X25519 public key |

The pinned-key exchange authenticates this descriptor; it has no standalone signature and must not be accepted from an unauthenticated JSON response.
Validate both public keys' encoding and length, and use the authenticated `expiresAt` rather than assuming a fixed key lifetime.
Cache the descriptor and replace it with authenticated updates from application responses as described below.
If it approaches expiry without an update, refresh through discovery, allowing for clock skew and request latency.
Seal application requests to the advertised public key.

After authenticating the response using an already trusted discovery key, clients may persist
`discoveryPublicKey` as their pin for future discovery. Never use it for application requests.
Older clients may ignore this field and keep using their existing pin while the server retains that key.

## Wire format and derivation

Request bytes, with no separators or length prefixes:

```text
(recipientKeyId XOR tag) (16) || clientEphemeralPublicKey (32) || nonce (24) || ciphertext (variable) || tag (16)
```

The logical header is `recipientKeyId (16) || clientEphemeralPublicKey (32)`. `recipientKeyId` is the first 16 bytes of SHA-512 of the raw 32-byte recipient X25519 public key (not its base64 text).
It selects the server key without transmitting that public key, for both discovery and application requests.
XOR the first 16 bytes with the tag after encryption; undo this before key lookup and HKDF, which uses the unmasked header.
Generate a fresh client ephemeral X25519 key pair for every exchange, including retries.
Encrypt the plaintext with [XAES-256-GCM](https://c2sp.org/XAES-256-GCM), using a fresh random 24-byte nonce and a 16-byte tag.

Derive 64 bytes with HKDF-SHA512:

```text
shared = X25519(clientEphemeralPrivateKey, recipientPublicKey)
info   = UTF8("MihuBot.Molly.MollyRequestProtector.v2") || header
keys   = HKDF-SHA512(IKM=shared, salt=empty, info=info, length=64)
requestKey  = keys[0..32]
responseKey = keys[32..64]
```

The server computes the same shared secret using the selected private key and the header's client ephemeral public key.
The recipient key ID and client ephemeral public key are included in HKDF's `info` through the header, so altering either header value changes the derived keys and prevents the existing ciphertext from authenticating.
The AEAD associated-data input is empty, and the UTF-8 label has no terminating NUL.

## Plaintext framing

Both requests and responses, including discovery, encrypt the following plaintext:

```text
paddingLength (BE16) || randomPadding (paddingLength bytes) || data (remaining bytes)
```

`paddingLength` is an unsigned 16-bit big-endian count of padding bytes, excluding the two-byte length field and the data.
The length field is always present; zero padding is encoded as `00 00`.
After authenticating and decrypting, skip the specified number of padding bytes and interpret the remaining bytes as data.
Reject frames with a missing length field or a padding length larger than the remaining plaintext.
The padding length and padding bytes are encrypted and authenticated along with the data.

Encrypt requests with `requestKey`.
The data is a UTF-8 JSON envelope containing `action`, `timestamp` (current Unix seconds), `nonce` and `data`.
The JSON `nonce` is standard padded base64 of 16 fresh random bytes and is separate from the AEAD nonce.
The server checks timestamp freshness and rejects recently used request nonces.

Response bytes are `nonce (24) || ciphertext || tag (16)`, encrypted with `responseKey`; the data after removing padding is a UTF-8 JSON `status`/`data` envelope.
The separate directional keys prevent a request ciphertext from authenticating as a response.

When an application request used an older in-memory key, its response also contains a top-level
`transportKey` object with the same fields as the discovery descriptor, regardless of the operation's status.
The server selects this update when encrypting the response, including if rotation occurred during the operation.
Authenticate the response before accepting the descriptor, and use its key for subsequent requests.
Responses to requests using the current key omit `transportKey`; discovery still returns its descriptor in `data`.
Regularly interacting clients can follow these updates without periodically repeating discovery.

## Key lifecycle and recovery

Rotating key pairs are independently generated, never derived from the static identity key.
The server generates a new key every 30 seconds; each is advertised as valid for 30 minutes.
Keys are retained for one additional minute of retirement grace; clients must still honor `expiresAt`.
Retirement prevents new lookups of a key but allows existing exchanges to finish; it is not a guarantee that key material has been erased from memory.
Transient session-key and plaintext byte buffers are cleared on a best-effort basis; this does not cover every exception path or managed JSON/string copy.
Discovery itself is not forward-secret: later disclosure of the identity private key reveals its public descriptors, not the rotating private keys.

A server restart can invalidate a cached key before its advertised expiry.
Unknown or retired recipient keys and invalid envelopes return HTTP 400 without executing an operation.
Clients may perform fresh authenticated discovery and retry once with a new ephemeral key, timestamp and nonce; never fall back to using the static key for application requests.
A transport timeout is different: the operation may already have succeeded, so retrying can duplicate it.
