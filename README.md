# I3X4Kusto
I3X API wrapper for Azure Data Explorer and Microsoft Fabric RTI.

## Mandatory Environment Variables
* "ADX_HOST": Azure Data Explorer or Fabric Event House endpoint
* "ADX_DB": Azure Data Explorer or Fabric Event House database name
* "ADX_APPLICATION_ID": Azure Entra ID application/client ID (only required when hosting I3X4Kusto on Azure)
* "AZURE_TENANT_ID": Azure Entra ID tenant ID

## Build Status
[![Docker](https://github.com/Azure-Samples/I3X4Kusto/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/Azure-Samples/I3X4Kusto/actions/workflows/docker-publish.yml)