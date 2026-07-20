# I3X4Kusto
I3X API wrapper for Azure Data Explorer and Microsoft Fabric RTI.

## Supported i3X 1.0 capabilities
This adapter implements the read/query and subscription surface of the [i3X 1.0 API](https://api.i3x.dev/v1/docs). The `GET /v1/info` endpoint advertises the exact capabilities:

- **Query** (`GET /objects`, `POST /objects/list|related|value|history`, `GET/POST` object & relationship types, `GET /namespaces`) - including historical value queries (`query.history = true`).
- **Subscriptions** (`POST /subscriptions`, `.../register`, `.../unregister`, `.../list`, `.../delete`, `.../sync`, `.../stream`) - the client creates a subscription, registers element ids, and then either polls `sync` or opens an SSE `stream`. Because Azure Data Explorer has no native change feed, both poll `opcua_telemetry` for values newer than each element's last-delivered timestamp (`subscribe.stream = true`). Subscription state is held in memory, so run a single replica.
- **Related-object queries** honor the `relationshipType` and its direction: forward containment types (`HasComponent`, `HasOrderedComponent`, `Organizes`, `HasProperty`) return an object's ISA-95 children; their reverses (`ComponentOf`, `OrganizedBy`, ...) return its parent. Relationships are derived from the OPC UA metadata's ISA-95 hierarchy (`Enterprise > Site > Area > Line > Workcell`), falling back to shared-parent (`NodeId`) siblings when those levels are absent.
- **Writes** (`PUT /objects/value|history`) are **not** implemented (`update.current = update.history = false`).

## Mandatory Environment Variables
* "ADX_HOST": Azure Data Explorer or Fabric Event House endpoint
* "ADX_DB": Azure Data Explorer or Fabric Event House database name
* "ADX_APPLICATION_ID": Azure Entra ID application/client ID (only required when hosting I3X4Kusto on Azure)
* "AZURE_TENANT_ID": Azure Entra ID tenant ID
* "I3X_BASIC_AUTH_USERNAME" / "I3X_BASIC_AUTH_PASSWORD": HTTP Basic authentication credentials. At least one authentication method (HTTP Basic and/or OAuth2) must be configured; see [Security](#security). If neither method is configured, the API fails closed and returns HTTP 503.

## Optional Environment Variables
* "I3X_CORS_ORIGINS": comma-separated list of allowed CORS origins. When unset, all origins are allowed (required so the browser-based CESMII i3X client can call the API cross-origin). Set this to lock CORS down to specific origins in production.
* "I3X_STREAM_POLL_MS": the SSE stream / sync poll interval against Azure Data Explorer, in milliseconds. Default 2000, minimum 250.
* "I3X_METADATA_CACHE_SECONDS": how long the ISA-95 object/namespace/type metadata (the `opcua_metadata_lkv` query that backs nearly every `/objects`, `/objecttypes` and `/namespaces` request) is cached in memory. OPC UA metadata changes rarely, so caching it sharply reduces the query volume against Azure Data Explorer or a Fabric Eventhouse and avoids `429 TooManyRequests` throttling on small capacities. Default 60, set to 0 to disable caching. Throttled queries are additionally retried with a short exponential backoff.
* "I3X_OAUTH2_AUTHORITY": the OpenID Connect authority (issuer) base URL. Setting this enables OAuth2 bearer-token authentication. See [Security](#security).
* "I3X_OAUTH2_AUDIENCE": expected audience (`aud`) claim(s) for OAuth2 access tokens; comma-separated for multiple values. When unset, the audience is not validated.
* "I3X_OAUTH2_ISSUER": expected token issuer. When unset, the issuer advertised by the authority's OIDC metadata is used.

## Security
Authentication is **mandatory** and **cannot be turned off**. Two authentication methods are supported, and a request is accepted if it satisfies **either** one:

1. **HTTP Basic** — the client sends an `Authorization: Basic <base64(username:password)>` header. Configured via `I3X_BASIC_AUTH_USERNAME` and `I3X_BASIC_AUTH_PASSWORD`.
2. **OAuth2 / OpenID Connect bearer tokens** — the client sends an `Authorization: Bearer <JWT>` header. Configured via `I3X_OAUTH2_AUTHORITY` (and optionally `I3X_OAUTH2_AUDIENCE` / `I3X_OAUTH2_ISSUER`). Access tokens are validated against the authority's OIDC metadata (issuer, signing keys/JWKS, audience and expiry). Signing keys are discovered and cached automatically.

You may configure one or both methods. If **neither** is configured, the API fails closed and returns HTTP 503. Regardless of configuration, the health/capabilities endpoint (`GET /v1/info`), the Swagger UI / OpenAPI documents, and CORS preflight (`OPTIONS`) requests remain open.

### Example: OAuth2 with Microsoft Entra ID
```bash
# Enable OAuth2 bearer-token authentication against an Entra ID tenant.
export I3X_OAUTH2_AUTHORITY="https://login.microsoftonline.com/<tenant-id>/v2.0"
export I3X_OAUTH2_AUDIENCE="api://<application-client-id>"
# Optional: pin the expected issuer (otherwise taken from the authority metadata).
export I3X_OAUTH2_ISSUER="https://login.microsoftonline.com/<tenant-id>/v2.0"
```

Clients then acquire a token from the authority and call the API with it:
```bash
curl -H "Authorization: Bearer $ACCESS_TOKEN" https://<host>/v1/objects
```

To use HTTP Basic instead (or in addition), set:
```bash
export I3X_BASIC_AUTH_USERNAME="i3x"
export I3X_BASIC_AUTH_PASSWORD="<strong-password>"
```

## Build Status
[![Docker](https://github.com/Azure-Samples/I3X4Kusto/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/Azure-Samples/I3X4Kusto/actions/workflows/docker-publish.yml)