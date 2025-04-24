# Discovery Relay

### Optimized Nostr relay to help Nostr scale globally

![Discovery Relay](discovery-relay.jpg)

Built in .NET for high performance, with ahead-of-time compilation, pre-defined types (not using Reflection). Utilizes LMDB for extreme performance. Key-Value storage with single table, avoiding supporting indexes. Supports only kind 3 and kind 10002, which is what Nostr clients should rely upon for discovery.

## Discovery Relays

Learn more: https://medium.com/@sondreb/discovery-relays-e2b0bd00feec

Also check out: https://medium.com/@sondreb/scaling-nostr-e50276774367

## Kudos

While mostly developed with LLM and own code, some inspiration and copy-paste (like error messages) was done from [Netstr](https://github.com/bezysoftware/netstr).
