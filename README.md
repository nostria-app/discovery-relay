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
  "StatsIntervalSeconds": 10
}
```

You must set the `ConnectionString` to your Azure Storage account connection string. The container will be automatically created if it doesn't exist.

## Kudos

While mostly developed with LLM and own code, some inspiration and copy-paste (like error messages) was done from [Netstr](https://github.com/bezysoftware/netstr).
