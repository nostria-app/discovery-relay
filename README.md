# Discovery Relay

### Optimized Nostr relay to help Nostr scale globally

![Discovery Relay](discovery-relay.jpg)

Built in .NET for high performance, with ahead-of-time compilation, pre-defined types (not using Reflection). Utilizes LMDB for extreme performance. Key-Value storage with single table, avoiding supporting indexes. Supports only kind 3 and kind 10002, which is what Nostr clients should rely upon for discovery.

## Discovery Relays

Learn more: https://medium.com/@sondreb/discovery-relays-e2b0bd00feec

Also check out: https://medium.com/@sondreb/scaling-nostr-e50276774367

## Features

- High performance
- Low memory usage
- Supports only kind 3 and kind 10002 (discovery)
- Multiple storage providers:
  - LMDB for key-value storage (default)
  - Azure Blob Storage for cloud deployments

If there is already an kind 10002 event for the same pubkey, the relay will not store an incoming kind 3 event.

## Configuration

### Storage Providers

The relay supports multiple storage providers that can be configured in the `appsettings.json` file:

```json
"Storage": {
  "Provider": "Lmdb", // Use "Lmdb" or "AzureBlob"
  "ApiAuthenticationGuid": "00000000-0000-0000-0000-000000000000"
}
```

#### LMDB Storage (Default)

LMDB is a high-performance embedded key-value store, which is the default storage provider:

```json
"Lmdb": {
  "DatabasePath": "./data",
  "SizeInMb": 1024,
  "MaxReaders": 4096,
  "StatsIntervalSeconds": 10,
  "ApiAuthenticationGuid": "00000000-0000-0000-0000-000000000000"
}
```

#### Azure Blob Storage

For cloud deployments, you can use Azure Blob Storage:

```json
"AzureBlob": {
  "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=yourstorageaccount;AccountKey=yourstoragekey;EndpointSuffix=core.windows.net",
  "ContainerName": "nostr-events",
  "StatsIntervalSeconds": 10,
  "UseManagedIdentity": false,
  "AccountName": "",
  "EndpointSuffix": "core.windows.net"
}
```

You can authenticate to Azure Blob Storage in two ways:

1. **Connection String**: Set the `ConnectionString` to your Azure Storage account connection string. 

2. **Managed Identity (recommended for Azure deployments)**: For higher security, use Azure Managed Identity by setting:

```json
"AzureBlob": {
  "ConnectionString": "",
  "ContainerName": "nostr-events",
  "StatsIntervalSeconds": 10,
  "UseManagedIdentity": true,
  "AccountName": "yourstorageaccount",
  "EndpointSuffix": "core.windows.net"
}
```

When using Managed Identity, make sure your Azure service (App Service, Azure Functions, VM, etc.) has been assigned a Managed Identity with proper permissions to access the storage account.

The container will be automatically created if it doesn't exist.

To run local blob storage:

```sh
npm install -g azurite
azurite
```

## Kudos

While mostly developed with LLM and own code, some inspiration and copy-paste (like error messages) was done from [Netstr](https://github.com/bezysoftware/netstr).
