# Documentation

This project is a self-hosted AT Protocol stack in .NET. It gives you a Personal Data Server (PDS), a Relay, and an AppView, then wires them together with .NET Aspire so you can watch real records flow through the system.

## Start here

1. [Primer](primer.md), learn atproto in plain language.
2. [How it works](how-it-works.md), follow one 🌤 emoji from click to live board.
3. [Architecture](architecture.md), see the exact service wiring.
4. [Reactive design](reactive-design.md), see why ingest is pull-based and Rx.NET stays in the AppView.
5. [Services](services.md), inspect each service surface.
6. [Scenarios](scenarios.md), see why you might use this.
7. [Packaging](packaging.md), see which libraries are extractable and how preview packages are built.

## Docs map

| Doc | What it gives you |
| --- | --- |
| [Primer](primer.md) | The core atproto ideas, explained with stable analogies and a glossary. |
| [How it works](how-it-works.md) | The life of an emoji status update, with requests, records, and diagrams. |
| [Architecture](architecture.md) | Topology, write path, read path, identity resolution, data model, and federation. |
| [Reactive design](reactive-design.md) | The .NET dataflow rule: pull ingest, `IObservable` seam, Rx projection, SignalR push. |
| [Services](services.md) | PDS, Relay, and AppView endpoint reference, config, and standalone run commands. |
| [Scenarios](scenarios.md) | Why the project exists, anchored on Statusphere and self-hosted atproto use cases. |
| [Packaging](packaging.md) | The nine extractable libraries, the layering that keeps the core service-free, and how preview packages are built. |
| [Implementation plan](plan.md) | Dense implementation log and milestone evidence. Start there only if you want history. |

If you are new to atproto, read the primer first. If you already know the protocol, jump to the architecture and services references.
