# <Episode title>

Status: awaiting approval

- Episode: `<nn>-<name>`
- Sources: `docs/<doc>.md`
- Target length: 10 min
- Viewer: a .NET developer who knows C# and has not used the library before.

## Promise

After watching, the viewer can <one or two concrete things>. One sentence on the problem the feature solves.

## Chapters

Ranked by importance. Budgets total the target length.

| # | Chapter | Budget | The viewer learns |
|---|---|---|---|
| 1 | Hook | 0:30 | the problem and a glimpse of the result |
| 2 | Setup | 1:30 | install the package and create the context |
| 3 | <Core feature> | 3:00 | <...> |
| 4 | Live sample | 2:30 | <...> |
| 5 | <Advanced topic> | 2:00 | <...> |
| 6 | Recap | 0:30 | what they can do now, optional pointers |
| | **Total** | **10:00** | |

### 1. Hook (0:30)

- Learns: <...>
- On screen: <components and the main motion, for example the live status page zooms out into a flow diagram>
- Sample code: none
- API: none

### 2. Setup (1:30)

- Learns: <...>
- On screen: <for example the context code types in, then morphs to add the feature>
- Sample code: `sample/Program.cs` regions `Context`, `ContextWithFeature`
- API: `InterceptorSubjectContext.Create()` (src/Namotion.Interceptor/...), `<Extension>()` (src/Namotion.Interceptor.<Feature>/...)

### 3. <Core feature> (3:00)

- Learns: <...>
- On screen: <...>
- Sample code: <file and regions>
- API: <members and source files>

<Repeat for every chapter.>

## Companion sample

- Projects: `sample/<Name>.Sample.csproj` (<what it hosts>)
- Ports: app <5300 + 10 × nn>, further apps and terminal captures <the following ports>
- Pages: `/` status page polling `/status` (<values shown>)
- Simulator: default, or scripted events (<event at time>)
- Demos: `<demo-name>` records <what happens, which end state it holds>
- Terminal captures: `<name>` runs <command>

## Coffee machine recap

<Where the episode recaps the domain and in how many beats, or "not needed".>

## Left out

| Doc section | Reason |
|---|---|
| <section> | <too advanced for this length, covered by another episode, no visual story> |

## Doc mismatches

<Places where the doc disagrees with the code, with file and member. "None" if there are none.>
