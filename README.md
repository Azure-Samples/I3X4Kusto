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

## Optional Environment Variables
* "I3X_CORS_ORIGINS": comma-separated list of allowed CORS origins. When unset, all origins are allowed (required so the browser-based CESMII i3X client can call the API cross-origin). Set this to lock CORS down to specific origins in production.
* "I3X_STREAM_POLL_MS": the SSE stream / sync poll interval against Azure Data Explorer, in milliseconds. Default 2000, minimum 250.

## Build Status
[![Docker](https://github.com/Azure-Samples/I3X4Kusto/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/Azure-Samples/I3X4Kusto/actions/workflows/docker-publish.yml)