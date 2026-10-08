# Connectors

Status: auto-approved (the user auto-approved both gates for this run)

- Episode: `03-connectors`
- Sources: `docs/connectors.md`, with `docs/connectors-websocket.md` for the WebSocket API of the live sample
- Target length: 10 min (acceptable 9 to 12)
- Viewer: a .NET developer who knows C# and has not used the library before.

## Promise

After watching, the viewer can tell a source from a server, wire a WebSocket server and client so a second process mirrors a model and writes back to it, and write a custom source on `SubjectSourceBase`. Connectors solve the problem of keeping an object model in sync with an external system in both directions, without hand-written polling, buffering or retry code.

## Chapters

Ranked by importance. Budgets total the target length; they were set after measuring the voice at about 210 words per minute.

| # | Chapter | Budget | The viewer learns |
|---|---|---|---|
| 1 | Hook | 0:30 | a model in one process, mirrored and written from another |
| 2 | Sources and servers | 1:30 | data ownership decides the role; the single-owner rule |
| 3 | Data flow | 1:35 | inbound through the property writer, outbound local first through the change queue |
| 4 | Live sample | 2:35 | embedded WebSocket handler and client source; a brew on the client runs on the server |
| 5 | Staying in sync | 1:50 | buffer, load, replay; write retry queue; local write wins; batching; source transactions |
| 6 | Your own source | 1:30 | `SubjectSourceBase` and its three hooks, ownership, registration |
| 7 | Recap | 0:25 | what they can do now, pointers to the protocol pages |
| | **Total** | **9:55** | |

### 1. Hook (0:30)

- Learns: a connector bridges a model to another system; here two processes share one coffee machine.
- On screen: two browser windows (server left, client right) spring in and play the `brew` clip; the camera moves to the client's Brew button, then to the server reacting.
- Sample code: none
- API: none

### 2. Sources and servers (1:30)

- Learns: the coffee machine recap; a connector bridges the subject tree to an external system; the role depends on who owns the data; source = replica of an external owner, server = exposes the local owner; each property has at most one source; `TryGetSource`.
- On screen: the machine tree as a top-down flow diagram with pulses (recap), then two flow rows (external owner, source, replica; local owner, server, clients) built step by step; dots mark claimed properties; a code card with `TryGetSource` from the client.
- Sample code: `sample/Client/Program.cs` region `SourceState`
- API: `PropertyReference.TryGetSource` (src/Namotion.Interceptor.Connectors/SourcePropertyExtensions.cs), `ISubjectSource.State`, `SourceState` (src/Namotion.Interceptor.Connectors/Monitoring)

### 3. Data flow (1:35)

- Learns: inbound values go external system, source, property writer, property; outbound writes change the local model immediately, are queued, and a background service calls `WriteChangesAsync`; the model is briefly ahead (local first); servers behave the same towards their clients.
- On screen: an inbound sequence diagram (3 messages), then an outbound one (4 messages), with the active participant highlighted and the camera following; a final pull back.
- Sample code: none (the sample code comes in the live chapter)
- API: `SubjectPropertyWriter.Write` (src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs), `ISubjectSource.WriteChangesAsync`

### 4. Live sample (2:35)

- Learns: embedded mode with `AddWebSocketSubjectHandler` and `MapWebSocketSubjectHandler`; the client with `AddWebSocketSubjectClientSource` and `ServerUri`; derived properties are computed on each side; a brew on the client is a local write that reaches the server, whose simulator runs the physics.
- On screen: server code types in, focus on the handler lines, morph to the mapping lines, morph to the client code; a terminal shows the client claiming properties; a flow diagram of the whole path with pulses; two browser windows play `heat` and then `brew`, camera moving between them; the client brew endpoint in a code card.
- Sample code: `sample/Server/Program.cs` regions `ServerSetup`, `MapHandler`; `sample/Client/Program.cs` regions `ClientSetup`, `BrewEndpoint`
- API: `AddWebSocketSubjectHandler<T>`, `MapWebSocketSubjectHandler`, `AddWebSocketSubjectClientSource<T>`, `WebSocketClientConfiguration.ServerUri` (src/Namotion.Interceptor.WebSocket/WebSocketSubjectExtensions.cs, Client/WebSocketClientConfiguration.cs), `WithFullPropertyTracking`, `WithRegistry`, `WithLifecycle`

### 5. Staying in sync (1:50)

- Learns: buffer, load, replay, reconcile on connect; the write retry queue (ring buffer, 1000 by default); a committed local write wins over the source's older value; outbound batching collapses each property to its settled value within the buffer time (8 ms); a source's own values are not echoed; source transactions confirm before the local apply.
- On screen: a four-step flow (Buffer, Load, Replay, Reconcile) revealed step by step; the retry queue as a stack of cards filling and draining; a collapse of three temperature cards into one; the client configuration code; the transaction code card; two cards comparing local first and confirmed.
- Sample code: `sample/Client/Configuration.cs` regions `RetryQueue`, `ConfirmedWrite`
- API: `WebSocketClientConfiguration.WriteRetryQueueSize`, `RetryTime`, `BufferTime`; `WithTransactions` (src/Namotion.Interceptor.Tracking), `WithSourceTransactions` (src/Namotion.Interceptor.Connectors), `BeginTransactionAsync`, `TransactionFailureHandling.Rollback`, `SubjectTransaction.CommitAsync`

### 6. Your own source (1:30)

- Learns: a grinder device that owns the grind size becomes a source; derive from `SubjectSourceBase`; override `StartListeningAsync`, `LoadInitialStateAsync`, `WriteChangesAsync`; claim ownership with `SourceOwnershipManager`; return `WriteResult.Failure` to use the retry queue; register as singleton and hosted service.
- On screen: a small flow diagram (grinder device, grinder source, bean hopper); the source class types in and each hook is focused in turn with camera moves; morph to the registration extension method.
- Sample code: `sample/Server/Grinder/GrinderSource.cs` regions `GrinderSourceClass`, `StartListening`, `LoadInitialState`, `WriteChanges`; `sample/Server/Grinder/GrinderSourceExtensions.cs` region `AddGrinderSource`
- API: `SubjectSourceBase`, `SubjectPropertyWriter.Write`, `BackgroundTaskLifetime.Start`, `SourceOwnershipManager.ClaimSource`, `WriteResult.Success`, `WriteResult.Failure`, `RegisteredSubjectProperty.SetValueFromSource` (all src/Namotion.Interceptor.Connectors)

### 7. Recap (0:25)

- Learns: the three ideas in one breath, then pointers to the WebSocket, MQTT and OPC UA pages.
- On screen: the roles diagram returns with pulses in both directions, then a closing chapter card.
- Sample code: none
- API: none

## Companion sample

- Projects: `sample/Server/Connectors.Server.csproj` (coffee machine, simulator, embedded WebSocket handler at `/ws`, grinder source with a simulated grinder device, status page) and `sample/Client/Connectors.Client.csproj` (mirrored machine via the WebSocket client source, status page, `POST /brew/{recipe}`)
- Ports: server 5310, client 5311, terminal capture of a second client 5312
- Pages: both serve `/` polling `/status` every 500 ms: role kicker, status headline, boiler temperature bar, pump pressure bar, cups brewed and water level; the client adds its connection state and a Brew Espresso button. Pages are designed for an 800 by 720 column so a split recording shows both side by side.
- Simulator: default `CoffeeMachineSimulatorService` on the server only; the client runs no simulator
- Demos: each records one split page (two iframes, server left, client right) at 1600 by 720 so both processes stay in sync on one clip.
  - `heat`: both apps start cold; the boiler heats from 20 °C to 93 °C on both sides; holds on Ready.
  - `brew`: waits for Ready, clicks Brew Espresso on the client; the server and client show Brewing Espresso, pressure rising to 9 bar, then Ready with one cup brewed; holds.
- Terminal captures: `run-client` runs a second client while both apps are running and stops after it claimed its properties.

## Coffee machine recap

Two beats at the start of chapter 2: the machine as a tree of tracked objects, and the simulator writing to it.

## Left out

| Doc section | Reason |
|---|---|
| Why the source does not always win (issue details, echo ordering) | condensed to one beat in chapter 5; the full reasoning is too deep for this length |
| Change notification source semantics (`ChangeOrigin`, stamping lifecycle) | internal detail; chapter 5 only says a source's own values are not echoed |
| Change batching contract details (revisions, emit order) and the Mermaid flowchart | the collapse and the echo skip are shown; the decision tree would need a long diagram with little payoff |
| Flushing on stop | operational detail, no visual story |
| Structural changes | advanced; deserves its own episode |
| Monitoring synchronization state, connector diagnostics | covered by the source monitoring page; only `TryGetSource` and `State` appear in chapter 2 |
| Inbound update error handling | short rule with no visual story |
| `ISubjectSource` direct implementation, diagnostics members | advanced; the base class path is the recommended one |
| Write retry queue configuration on the base constructor, `BackgroundTaskLifetime` details | shown in the custom source code, not discussed separately |
| Low-level ownership API (`SetSource`, `RemoveSource`) | `SourceOwnershipManager` covers the common case |
| Servers: `SubjectConnectorBase` implementation pattern | the built-in servers are the reference; a custom server is rare |
| Property mappers, path providers, subject updates | protocol-specific mapping, covered by each protocol page |
| Thread safety | one sentence in the docs, no visual story |
| Known limitations | too detailed for an introduction |

## Doc mismatches

- `docs/connectors.md` "Custom Source Example" and "SourceOwnershipManager" use `_logger` inside the derived `DatabaseSource`, but `SubjectSourceBase._logger` is private (src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs). A derived source must keep its own logger field.
- `docs/connectors.md` "Data Flow" and the hooks table describe `propertyWriter.Write()` without arguments; the real signature is `Write<TState>(TState state, Action<TState> update)` (src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs).
- `docs/connectors-websocket.md` "Reconnection" says the base class reconciles queued writes by commit order after a reconnect, while `docs/connectors.md` "Known Limitations" states that connector-internal reconnects (the WebSocket monitor among them) skip the reconcile and do not flush the retry queue (#362). The code follows the limitation: `WebSocketSubjectClientSource.RunMonitorLoopAsync` calls `LoadInitialStateAndResumeAsync` only.
