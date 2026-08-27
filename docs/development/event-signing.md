# Webhook event signing (inbound)

Every `POST /api/events` request must prove it was sent by someone holding the tenant's
signing secret (Decision #23). The API rejects any signed request that fails verification with
`401` **before** a single database write.

## Scheme

Two request headers on every event POST:

| Header | Format | Meaning |
|--------|--------|---------|
| `X-SignalForge-Timestamp` | Unix seconds (e.g. `1789246753`) | Time the signature was produced; must be within ±300s of server time (replay protection) |
| `X-SignalForge-Signature` | `sha256=<64 lowercase hex chars>` | HMAC-SHA256 over `"{timestamp}:{rawBody}"` using the tenant's signing secret |

The signature always covers the **exact raw request body bytes** that are transmitted — never a
re-serialized copy. Any whitespace or encoding change invalidates it.

## Computing a signature (curl)

```bash
# The API key proves identity; the signing secret proves message integrity + authenticity.
export SF_API_KEY="<api key>"
export SF_SIGNING_SECRET="<tenant signing secret>"

BODY='{"externalEventId":"order-001","eventType":"order.created","payload":"{\"orderId\":42}"}'
TS=$(date +%s)
SIG="sha256=$(printf '%s:%s' "$TS" "$BODY" | openssl dgst -sha256 -hmac "$SF_SIGNING_SECRET" | awk '{print $2}')"

curl -i -X POST http://localhost:8080/api/events \
  -H "X-API-Key: $SF_API_KEY" \
  -H "Content-Type: application/json" \
  -H "X-SignalForge-Timestamp: $TS" \
  -H "X-SignalForge-Signature: $SIG" \
  --data-binary "$BODY"
```

Getting the body right matters: the string passed to `--data-binary` must be byte-identical to the
string used in `printf`. Serving clients should sign the same buffer they send (see the
.NET `EventSigner` helper in the integration tests).

## Getting the secret

- `docker compose up -d --build` prints the seeded secret once to the API container logs
  (`[Seed] ... Webhook signing secret for local dev: ...`) or read `.env` / appsettings override
  `Seed:SigningSecret`.
- In tests, every host that seeds the shared tenant must supply a **deterministic**
  `Seed:SigningSecret` (e.g. `ApiTestFactory.SigningSecret`); a random one leaks into tests that
  share the tenant and breaks their signature verification (see Decision #23 Consequences).

## Verification behavior

| Condition | Result |
|-----------|--------|
| Signed correctly, timestamp fresh | `201 Created` |
| Missing/invalid timestamp header | `401 Unauthorized` |
| Missing signature header | `401 Unauthorized` |
| Tampered body | `401 Unauthorized` (HMAC mismatch) |
| Wrong/rotated secret | `401 Unauthorized` (HMAC mismatch) |
| Timestamp older/newer than ±300s | `401 Unauthorized` (replay) |

The comparison uses `CryptographicOperations.FixedTimeEquals` (constant time), and the secret is
recoverable server-side only (it is never logged, echoed, or stored hashed — hashing would make
verification impossible).

## Implementation

- `src/SignalForge.Application/Security/EventSignatureVerifier.cs` — pure algorithm (HMAC, parse, verify).
- `src/SignalForge.Api/Middleware/EventsSignatureMiddleware.cs` — HTTP enforcement after
  authentication, before controllers.
- `src/SignalForge.Domain/Models/TenantWebhookSigningSetting.cs` — per-tenant secret storage.
- `docs/development/environment.md` — local dev/env configuration.