# Tracking

Status: auto-approved (the user auto-approved both gates for this run)

- Episode: `01-tracking`
- Sources: `docs/tracking.md`, `docs/tracking-transactions.md`
- Target length: 10 min (acceptable 9 to 12)
- Viewer: a .NET developer who knows C# and has not used the library before.

## Promise

After watching, the viewer can turn on tracking for a model, write derived properties that update themselves, observe every change through a change stream or a per-property subscription, react to subjects joining and leaving the object graph, and wrap a multi-step write in a transaction so it lands completely or not at all. Tracking solves the problem of keeping everything that depends on a model (screens, computed values, connectors) correct without hand-written change events.

## Chapters

Ranked by importance. Budgets total the target length, at about 2.85 words per second of finished video for `kokoro:am_michael` at tempo 1.32.

| # | Chapter | Budget | The viewer learns |
|---|---|---|---|
| 1 | Hook | 0:35 | every write becomes an observable change, derived values update themselves, and a failed brew can leave a machine stuck |
| 2 | Setup | 1:10 | the coffee machine recap; one line of context setup and what it bundles |
| 3 | Derived properties | 1:50 | `[Derived]`, recorded dependencies, the ripple from a temperature write to `IsReady` and `Status`, publishing only real changes |
| 4 | Change streams | 2:00 | what a change carries, the observable, per-property subscriptions, the queue; a brew is six changes in write order |
| 5 | Lifecycle | 1:15 | a recipe added to the graph is attached, inherits the context and is tracked; lifecycle events and their rules |
| 6 | Transactions | 2:30 | a plain multi-step write can stop halfway; `BrewAsync` captures the writes and commits them all or none |
| 7 | Recap | 0:30 | what they can do now, optional pointers |
| | **Total** | **9:50** | |

### 1. Hook (0:35)

- Learns: tracking turns every property write into a change; derived values follow; transactions make a multi-step write all or nothing.
- On screen: chapter card; two windows (machine status left, change stream right) play the `heat` clip while the change rows stream in; the camera moves from the status headline to the counters; then the `stuck` clip: the left machine stuck on "Brewing Ristretto", the right one still Ready.
- Sample code: none
- API: none

### 2. Setup (1:10)

- Learns: the coffee machine is a tree of tracked objects written by a simulator (recap); `WithFullPropertyTracking()` bundles equality checks, derived property detection, change subscriptions and context inheritance with lifecycle; children inherit the context; transactions are opt-in.
- On screen: the machine tree as a top-down flow diagram (machine, boiler, pump, water tank, bean hopper, recipes); a simulator node pulses writes into the boiler; a code card types the context setup, four feature pills arrive beside the tracking line; pulses run down the tree when the machine line is focused.
- Sample code: `sample/Steps.cs` region `Context`
- API: `InterceptorSubjectContext.Create()` (src/Namotion.Interceptor/InterceptorSubjectContext.cs), `WithFullPropertyTracking()` (src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs), `WithDataAnnotationValidation()` (src/Namotion.Interceptor.Validation)

### 3. Derived properties (1:50)

- Learns: a `[Derived]` property is a plain getter; the library records the properties it reads, including other derived properties, flattened to their sources; a dependency write recalculates it; it publishes a change only when the value changed; dependencies are recorded on each evaluation, so a branch not taken is no dependency; the equality check drops writes of the same value.
- On screen: code card with the `Derived` region of `CoffeeMachine.cs`, focus on `IsReady`, then `Status`; morph to `IsHot` in `Boiler.cs`; a dependency flow diagram (Temperature, TargetTemperature, IsHot, IsReady, Status) with pulses rippling from a temperature write; the `heat` clip with the change counters (Temperature, Status, IsReady) climbing at different rates; pills "10 writes per second", "once per degree", "once".
- Sample code: `../../domain/Coffee/CoffeeMachine.cs` region `Derived`, `../../domain/Coffee/Boiler.cs` region `IsHot` (new, additive)
- API: `[Derived]` (src/Namotion.Interceptor/Attributes), `WithDerivedPropertyChangeDetection()`, `WithEqualityCheck()` (src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs)

### 4. Change streams (2:00)

- Learns: a `SubjectPropertyChange` carries the property, old and new value, timestamp and revision; three channels share one write path; `GetPropertyChangeObservable()` delivers on a scheduler and composes with Rx; one brew is six changes in the order `Brew` wrote them, including a status without a recipe name; `SubscribeToProperty` watches one property of one subject on a scheduler (inline runs inside the setter); `CreatePropertyChangeQueueSubscription()` for a dedicated consumer thread; under concurrent writers, compare `Revision` or re-read.
- On screen: a change card with its fields; a fan-out flow from the write to three channels; the `ChangeStream` code card (observable with `Where`); the `brew` clip with the six rows; the `ReadyLog` subscription code; a terminal running the sample until it logs that the machine is ready; the queue code from `Steps.cs`.
- Sample code: `sample/ChangeStream.cs` region `Observable`; `sample/MachineHost.cs` region `ReadyLog`; `sample/Steps.cs` region `Queue`
- API: `GetPropertyChangeObservable`, `CreatePropertyChangeQueueSubscription` (src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs), `SubscribeToProperty` (src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs), `SubjectPropertyChange.GetOldValue`, `GetNewValue`, `Revision` (src/Namotion.Interceptor.Tracking/Change/SubjectPropertyChange.cs), `PropertyChangeQueueSubscription.TryDequeue`

### 5. Lifecycle (1:15)

- Learns: recipes are subjects; assigning a new `Recipe` attaches it: it inherits the context and its writes are tracked; removing the last reference detaches it (a removed branch detaches its whole subtree); `SubjectAttached` and `SubjectDetaching` events; handlers run inside a lock and must be fast and must not throw.
- On screen: the machine tree with Recipes, Espresso and Lungo; the `AddRecipe` endpoint code; a Ristretto node springs into the tree with an "attached" pill and a context dot; the `Lifecycle` code; the `recipe` clip with the attached row in the change stream and the new recipe button.
- Sample code: `sample/Program.cs` region `AddRecipe`; `sample/MachineHost.cs` region `Lifecycle`
- API: `LifecycleInterceptor.SubjectAttached`, `SubjectDetaching`, `TryGetLifecycleInterceptor` (src/Namotion.Interceptor.Tracking/Lifecycle), `SubjectLifecycleChange.Subject`

### 6. Transactions (2:30)

- Learns: `Brew` is four separate writes; a write that throws halfway (the Ristretto asks for 97 °C, the boiler accepts 85 to 96) leaves the machine stuck in Brewing with the pump off, and it refuses the next espresso; `WithTransactions()` and `BeginTransactionAsync(TransactionFailureHandling.Rollback)`; writes inside are captured, reads see the pending values, nothing is published; `CommitAsync` replays the writes in order and publishes each change then; a transaction disposed without commit discards everything; `Rollback` reverts already applied writes if one fails during commit; exclusive locking runs transactions one after another; limits (one context, no nesting, same async flow).
- On screen: the `Brew` region code card; a write timeline of four chips where the third turns pink; the `Boiler` range highlighted; the `stuck` clip, left machine; the code morphs into `BrewAsync`; the context morphs to add `WithTransactions()`; captured writes stack in a pending box and land together on commit; the `stuck` clip, right machine stays Ready and then brews.
- Sample code: `../../domain/Coffee/CoffeeMachine.cs` regions `Brew`, `BrewAsync` (new, additive); `../../domain/Coffee/Boiler.cs` region `Boiler`; `sample/MachineHost.cs` region `ContextWithTransactions`
- API: `WithTransactions()` (src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs), `BeginTransactionAsync`, `TransactionFailureHandling.Rollback`, `SubjectTransaction.CommitAsync` (src/Namotion.Interceptor.Tracking/Transactions), `IInterceptorSubject.Context` (src/Namotion.Interceptor/IInterceptorSubject.cs)

### 7. Recap (0:30)

- Learns: the four ideas in one breath; pointers to the connectors episode for source transactions and to the docs for the queue and lifecycle details.
- On screen: the dependency ripple, the change rows and the transaction box return in quick succession; a closing chapter card.
- Sample code: none
- API: none

## Companion sample

- Projects: `sample/Tracking.Sample.csproj`, one ASP.NET app hosting two coffee machines, each in its own context with tracking, validation and transactions, and each with its own simulator: `brew` brews with the plain `Brew`, `brew-async` with `BrewAsync`.
- Ports: app 5320, terminal capture 5321
- Pages, each an 800 by 720 column for split recordings, polling every 250 ms:
  - `/{machine}/` machine page: method name kicker, ready pill, status headline, boiler and pump bars, one button per recipe, an error line for rejected brews.
  - `/{machine}/changes` change stream page: counters for `Boiler.Temperature`, `Status` and `IsReady`, and the latest changes (newest on top) without the simulator's physics noise (temperature, pressure, heater, levels), plus lifecycle rows.
- Endpoints: `GET /{machine}/status`, `GET /{machine}/stream`, `POST /{machine}/brew/{recipe}` (400 with the message when the brew is refused), `POST /{machine}/recipes` (adds the Ristretto at 25 ml and 97 °C), and the demo helper `POST /{machine}/reset` (cold boiler, idle, original recipes, empty stream).
- Simulator: default `CoffeeMachineSimulatorService` per machine.
- Demos (split pages, two iframes, 1600 by 720):
  - `heat`: machine and stream of `brew`; reset, heat from 20 °C to Ready; marks `open`, `warm`, `ready`; holds.
  - `brew`: machine and stream of `brew`; click Espresso; six rows; pressure 9 bar; one cup; marks `open`, `click`, `pressure`, `done`; holds.
  - `recipe`: machine and stream of `brew`; add the Ristretto; the attached row and the new button; marks `open`, `add`; holds.
  - `stuck`: machine `brew` left and machine `brew-async` right; Ristretto on the left gets stuck; Ristretto on the right is rejected and stays Ready; Espresso on both: the left refuses, the right brews; marks `open`, `left`, `right`, `espresso`, `brewing`, `done`; holds.
- Terminal captures: `run-sample` runs the sample on 5321 until a machine logs that it is ready.

## Coffee machine recap

Two beats at the start of chapter 2: the machine as a tree of tracked objects, and the simulator writing to it.

## Left out

| Doc section | Reason |
|---|---|
| Queue semantics and threading, queue limitations | one beat on when to use the queue; the details are reference material |
| Per-property delivery details (scheduler rules, `PendingCount`, error routing, disposal races) | one beat on inline versus scheduled; the rest is reference material |
| Concurrency and delivery, delivery guarantees table | condensed to one beat on `Revision` and re-reading |
| Manual recalculation (`RecalculateDerivedProperty`) | rare case without a visual story in the domain |
| Parent tracking (`WithParents`), read property recorder | not needed for the story; internal or niche |
| Change origin and timestamps (`SetValueFromSource`, `WithChangedTimestamp`) | belongs to connectors; covered by the connectors episode |
| Reference counting details, cycles | one beat on subtree detach; cycles are an edge case |
| Transactions: optimistic locking, conflict behavior, requirements, commit timeout | advanced; the exclusive default is named in one beat |
| Transactions: source transactions, commit flow with sources, failure flow tables, custom transaction writer | belongs to connectors; one pointer in the recap |
| Transactions: last write wins, write order limitation, pending change inspection | reference material without a visual story |

## Doc mismatches

- `docs/tracking.md` "Transactions" says changes are "applied together on commit, with change notifications fired after all changes are applied", and `docs/tracking-transactions.md` "Without Source Transactions" lists "Fire change notifications" as a step after applying all changes. The code replays the captured writes one by one (`SubjectPropertyChangeOperations.ApplyLocalChanges` calls each setter in insertion order) and each write publishes its change and recalculates its derived properties immediately, as the "Capture and Commit Replay" section of `docs/tracking-transactions.md` describes. An observer of a committed `BrewAsync` sees the same six changes as for `Brew`, including the derived `Status` "Brewing " before the recipe name lands. The video follows the code: a transaction makes the writes all or nothing, it does not hide the intermediate notifications of the commit. If notification after the whole commit is the intended contract, this is a library defect.
- `docs/tracking.md` "Key features" lists "Atomic commits: All changes applied together", which reads as atomic visibility; the code gives all-or-nothing outcomes and serialization of transactions, not atomic visibility to observers.
- `docs/tracking.md` "Transactions" uses `TransactionFailureHandling.BestEffort` in the basic example while the transactions page recommends `Rollback` for full atomicity; not wrong, but the video uses `Rollback` because a brew must land completely or not at all.
