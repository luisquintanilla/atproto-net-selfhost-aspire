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
8. [Extending](extending.md), build on the stack: reuse a library, add a lexicon, build a projection, swap storage, compose a topology.
9. [Storage profiles](storage.md), run the durable production profile (SQLite PDS) instead of in-memory.
10. [Analytics view](analytics.md), the DuckDB OLAP projection that production adds over the same firehose.

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
| [Extending](extending.md) | The cookbook for building on the stack: reuse a library, define a lexicon, index a different collection, build your own projection, add an endpoint, swap storage, compose a topology, and what is left for production. |
| [Storage profiles](storage.md) | The dev (in-memory, default) vs production (durable SQLite PDS) profiles: the toggle, the SQLite schema, and what survives a restart. |
| [Analytics view](analytics.md) | The DuckDB OLAP projection production adds: a second AppView over the same firehose for totals, rates, top-N, and breakdowns. |
| [Implementation plan](plan.md) | Dense implementation log and milestone evidence. Start there only if you want history. |

If you are new to atproto, read the primer first. If you already know the protocol, jump to the architecture and services references.
