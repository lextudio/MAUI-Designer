# OpenDevelop addin + DDP out-of-process integration

Status: **in progress**. This document is the design of record and the progress log.
Updated as each phase lands.

## 1. Goal

Ship `MAUIDesigner` as an **OpenDevelop addin** whose design surface is an
**out-of-process (OOP) designer** speaking the shared **Designer Protocol (DDP)**, so
OpenDevelop can drive it exactly the way it already drives the WPF, WinUI and Forms
designers.

The deliverable is *not* a new protocol. OpenDevelop already ships the protocol, the
transport and the host process lifecycle; this work is an adapter.

## 2. Hard constraints

| Constraint | Consequence |
|---|---|
| The original standalone app and its tests are Windows/WinUI-specific | They currently target `net10.0-windows10.0.19041.0`, set `win-x64`, and call `Windows.Foundation` / `Windows.System` APIs directly. They cannot be the macOS implementation by merely changing a TFM. |
| macOS must be native AppKit, not Mac Catalyst | Use the experimental MAUI Labs package `Microsoft.Maui.Platforms.MacOS` with `net10.0-macos` and its `MacOSMauiApplication` bootstrap. This is a true AppKit host (`NSApplication` / `NSWindow`), not an iPad/UIKit app repackaged for macOS. |
| The MAUI Labs AppKit backend is experimental | Pin an explicit compatible prerelease package version; do not use a floating `*` version in a distributable addin. Treat package/handler upgrades as a platform-backend compatibility test event. |
| The DDP canvas methods are inherently platform-bound | `design/hit-test`, `design/render`, `design/export-png`, `design/theme`, `design/popup`, and visual-state reporting belong in a platform host, with a shared contract and shared document state. |
| The addin compiles against OpenDevelop assemblies | The referenced OpenDevelop version must be **≤ the host's version**, or the addin fails to load (SD-1406 "more recent version"). Build against a matching `OpenDevelopRoot` (v5.5.8 worktree), never against main HEAD. |
| An external addin is never copied into the app bundle | Debug via the Addin SDK's `-addindir:` launch, never by deploying over `/Applications`. |

**Therefore this repository must have two first-class native hosts:** Windows keeps the
existing WinUI implementation, and macOS uses the MAUI Labs AppKit implementation. The
document model, DDP service/client, geometry, XAML round trip, and their tests remain below
both hosts at `net10.0`.

### 2a. Cross-platform host plan (2026-09-28)

The initial implementation was deliberately split at DDP so the difficult protocol work could
be proven on macOS. That work is now complete: the dialect suite passes 19 tests, the UI-free
DDP service suite passes 31 tests, and the real spawned-child loopback JSON-RPC suite passes 5
tests on macOS. The remaining work is host integration, not protocol invention.

The old statement that rendering is Windows-only is now superseded. The target architecture is:

```text
                     MAUIDesigner.Fresh.Core / DDP / Surface (net10.0)
                                           |
                         shared host-facing abstractions
                              /                         \
        net10.0-windows10.0.19041.0                 net10.0-macos
              WinUI native adapter               MAUI Labs AppKit adapter
```

The app and child host must not contain `#if` blocks scattered through designer behaviour.
Instead, isolate the existing direct WinUI calls behind small platform adapters for pointer
capture, keyboard modifiers/keys, view-to-screen bounds, sidebar resize drawing, and window
preview. Provide equivalent AppKit adapters using MAUI Labs handlers on macOS. The shared
designer continues to reason only in design coordinates and DDP DTOs.

Relevant MAUI Labs references:

- https://github.com/dotnet/maui-labs/blob/main/platforms/MacOS/README.md
- https://learn.microsoft.com/en-us/dotnet/maui/developer-tools/platform-backends/?view=net-maui-10.0

## 3. What already exists and must not be rewritten

All of the following come from the OpenDevelop tree and are reused as-is:

| Concern | Type | Notes |
|---|---|---|
| All DDP DTOs | `ICSharpCode.SharpDevelop.Designer.Remote.DesignerProtocol` | `DesignerDocumentSnapshot`, `DesignerSessionState`, `DesignerElementNode`, `DesignerRenderFrame`, `DesignerHitTestResult`, `DesignerEditSet`, `DesignerPropertyInfo`, `DesignerToolboxItemInfo`, `DesignerCapabilities`, … `DesignerProtocol.Version = 2` |
| Client seam | `IDesignHostClient` | ~20 methods (Open/Update/Flush/SetProperty/AddElement/Delete/Rename/HitTest/ApplyLayout/SetZOrder/Render/ExportPng/…) |
| Child bootstrap | `ICSharpCode.SharpDevelop.Designer.Remote.DesignerChildHost.Run(args, prefix, createService, afterShutdown)` | Reads `--port`/`--token`, connects back over loopback TCP, attaches `[JsonRpcMethod]` by reflection |
| Child service seam | `IDesignerChildService` | Only `WaitForShutdown()` + `OnParentDisconnected()` — **no** interface to implement for DDP |
| Host launch | `DesignerHostProcessClient` | `dotnet exec <host>.dll --port N --token T` |
| Shared host pooling / recovery | `SharedDesignerHostPool`, `SharedDesignerHostBroker`, `SharedDesignerHostRecovery` | |
| Frame codec / RPC formatting | `DesignerFrameCodec`, `DesignerJsonRpc` | |

Both `Designer.Remote` and `Designer.Server` target `net9.0;net10.0` with **no UI
dependency**, so the protocol layer is consumable — and testable — on macOS.

## 4. New projects

Placed in this repository under `maui-designer-native/`, alongside the existing
`MAUIDesigner.Fresh.*` projects (same arrangement as CoreWF, which ships its own
`src/OpenDevelop.AddIn/WorkflowDesigner*` tree).

```
maui-designer-native/
  MAUIDesigner.Fresh.Core/          net10.0            (existing) document model, commands,
                                                              history, geometry, drop targets
  MAUIDesigner.Dialect/             net10.0            (new) is-this-file-MAUI? evidence
  MAUIDesigner.Dialect.Tests/       net10.0            (new) dialect tests             [macOS OK]
  MAUIDesigner.Ddp/                 net10.0            (new) DDP service logic, UI-free
  MAUIDesigner.Ddp.Tests/           net10.0            (new) service + mapping tests  [macOS OK]
  MAUIDesigner.Host/                net10.0-windows +    (new) child entry and native canvas;
                                    net10.0-macos              WinUI on Windows, AppKit on macOS
  ICSharpCode.MauiDesigner.AddIn/   net10.0-windows     (new) in-process addin: display
                                                              binding, view content, pads
  global.json / NuGet.config                             (new) see "Build entry point" below
```

`MAUIDesigner.Ddp` is deliberately UI-free: it holds the whole DDP method surface
except the canvas operations, so **the bulk of the adapter is buildable and testable on
both macOS and Windows**. `MAUIDesigner.Host` is a thin platform shell with a shared
DDP-facing service and separate WinUI/AppKit render and input adapters.

`MAUIDesigner.Dialect` is UI-free for the same reason, and for a second one: a
`net10.0-windows` test project **cannot run on macOS at all** (no
`Microsoft.WindowsDesktop.App` runtime), so anything worth testing has to live below
`net10.0-windows`. This is exactly why OpenDevelop keeps `Designer.Remote` at
`net9.0;net10.0`.

Reused source is brought in by **link**, not by a cross-repo project reference, so the
MAUI tree does not need to import `MAUIDesigner.Fresh.Core.csproj` into the OpenDevelop
solution:

```xml
<Compile Include="..\MAUIDesigner.Fresh.Core\Documents\**\*.cs" Link="Core\Documents\%(RecursiveDir)%(Filename)%(Extension)" />
```

(For Phase 1 a `ProjectReference` to `MAUIDesigner.Fresh.Core.csproj` is simpler and
equivalent; the switch to `Link` happens when the DDP service must be compiled *into* the
Windows host without dragging the Core project's own settings along.)

### Build entry point

An out-of-tree addin needs two files the MAUI tree did not have before:

- **`global.json`** pinning `LibreWPF.Sdk`. MSBuild resolves `global.json` **once, from the
  build's entry point**, so building the addin from this repository never consults
  OpenDevelop's own `global.json` and the SDK resolver fails with
  *"no version specified in the project or global.json"* (MSB4236). Keep the version in
  lock-step with OpenDevelop's. No `"sdk"` section — pinning the .NET SDK here would
  constrain every other project in the repository.
- **`NuGet.config`** adding the `librewpf-local` feed. OpenDevelop resolves `LibreWPF.*`
  from a local feed, not nuget.org, and the relative path in OpenDevelop's `nuget.config`
  does not apply from here. `packageSourceMapping` is required, otherwise every package
  asked for fails to resolve. nuget.org is also listed so the existing MAUI projects still
  restore.

## 5a. Which XAML file does the MAUI designer own?

This is the one place the addin is **not** self-sufficient today, and the reason is
structural rather than accidental:

- `DisplayBindingService.AttachSubWindows` iterates **every** secondary binding and attaches
  each one whose `CanAttachTo` returns true. There is no "first wins" and no exclusivity.
- `WpfSecondaryDisplayBinding.CanAttachTo` only *excludes* `WinUI` and `Uno` (via
  `XamlFrameworkDetector`) and the root names `ResourceDictionary` / `Application` /
  `Activity`. Everything else — including `Unknown` — is claimed.
- MAUI's roots (`ContentPage`, `ContentView`, `Grid`, …) are on neither list, so **both** the
  WPF and MAUI design surfaces would attach to one MAUI file.

CoreWF, the one existing out-of-tree designer, does not hit this: it deliberately chose a
root element (`Activity`) that WPF's exclusion list already contains, and additionally
returns `Array.Empty<IViewContent>()` when it has already attached. The first half is luck
rather than design; the defensive half is worth copying and is copied.

The built-in designers also assume a closed, co-located set — `AvalonEdit.AddIn.csproj`
carries direct `ProjectReference`s to `WpfDesign.AddIn` and `WinUIXamlDesigner.AddIn`, and
`XamlFrameworkDetector` hard-codes the three built-in dialects. An out-of-tree designer
cannot participate in build-time coordination, so ownership has to be declared at runtime.

**Implemented** in OpenDevelop as `XamlDialectRegistry` + `IXamlDialectDisplayBinding` (see OpenDevelop's `doc/technotes/designer-common.md`, "Dialect ownership"). The shape, and the reason it is one-time rather than per-framework:

```csharp
// OpenDevelop, new file: any addin may register a dialect it owns.
public interface IXamlDialectDisplayBinding : ISecondaryDisplayBinding
{
    IEnumerable<string> Dialects { get; }
}

// DisplayBindingService.AttachSubWindows gains a guard of ~6 lines, before CanAttachTo:
//   if the binding declares dialects and the file's dialect is not one of them, skip it.
//   An Unknown dialect is not filtered, so today's permissive behaviour is preserved.
```

Because the routing key is an **open string**, `XamlFrameworkKind` and the four places that
switch on it stay exactly as they are — the enum remains a compile-time convenience for the
built-ins and is still exhaustive for them. Adding MAUI, and any later dialect, then costs
one registration in the MAUI addin and **no edit to WPF's or WinUI's binding, to the
detector, or to the workbench**. `MauiDesignerDisplayBinding.Dialects` is already declared
so that step is a one-liner.

## 5. DDP → Core mapping

`Core` is a near-1:1 fit for the document operations. Every mutating DDP method maps to a
public `IDocumentCommand` record executed through `DocumentSession.Execute`, which also
yields undo/redo for free; `DesignerXamlReader`/`Writer` carry state as XAML text.

> **`DocumentEditor` is `internal`.** The obvious-looking entry points
> (`DocumentEditor.SetProperty`, `.Add`, `.SetBounds`, …) are not reachable from another
> assembly. The public surface is the command records plus `DocumentSession.Execute`:
> `AddElementCommand`, `RemoveElementCommand`, `ReparentElementCommand`,
> `ReorderElementCommand`, `PlaceElementCommand`, `SetPropertyCommand`, `SetBoundsCommand`,
> `CompositeDocumentCommand`, `ReplaceDocumentCommand`. Do not reach for `InternalsVisibleTo`.

| DDP method | `IDesignHostClient` | Core landing site | Layer |
|---|---|---|---|
| `initialize` | handshake | token check + `HostHandshake` (`DesignerProtocol.Version`) | Ddp |
| `session/open` | `OpenAsync` | `DesignerXamlReader.Read` → `Validate` → `new DocumentSession` | Ddp |
| `session/update` | `UpdateAsync` | same path (a reload replaces the document) | Ddp |
| `session/flush` | `FlushAsync` | `DesignerXamlWriter.Write(session.Current)` → `DesignerEditSet` | Ddp |
| `design/set-property` | `SetPropertyAsync` | `SetPropertyCommand` | Ddp |
| `design/reset-property` | `ResetPropertyAsync` | `SetPropertyCommand` with a `null` value (removes the attribute) | Ddp |
| `design/set-bounds` | `SetBoundsAsync` | `SetBoundsCommand` | Ddp |
| `design/add-element` | `AddElementAsync` | `AddElementCommand`, template parsed from `DesignerToolboxItemInfo.Template` | Ddp |
| `design/delete-elements` | `DeleteElementsAsync` | `CompositeDocumentCommand(RemoveElementCommand…)`, deepest id first so a parent+child request does not throw | Ddp |
| `design/rename` | `RenameAsync` | `SetPropertyCommand` on `x:Name` | Ddp |
| `design/set-zorder` | `SetZOrderAsync` | `ReorderElementCommand` | Ddp |
| `design/set-event` | `SetEventAsync` | `SetPropertyCommand` on the event attribute — MAUI binds a code-behind method by name, so the handler *is* the value | Ddp |
| `session/close` | `CloseAsync` | release the `DocumentSession` and clear the session identity | Ddp |
| `design/hit-test` | `HitTestAsync` | real visual hit test | **Host** |
| `design/render` | frame | MAUI view render | **Host** |
| `design/export-png` | `ExportPngAsync` | MAUI view render | **Host** |
| `design/theme` | `SetThemeAsync` | `AppResourcesRequested` | **Host** |
| `design/app-resources` | `SetAppResourcesAsync` | MAUI `Application.Resources` | **Host** |
| popup / visual state | — | MAUI realized visuals | **Host** |
| `design/apply-layout` | `ApplyLayoutAsync` | `ReorderElementCommand` + `SetBoundsCommand`; `Core`'s `DropTargetResolver`/`GridGeometry` cover the geometry | **deferred, see below** |
| `design/activate-default-event` | `ActivateDefaultEventAsync` | MAUI has no single "default" event per control | **deferred** |

**The protocol table in `designer-common.md` is a superset, not a checklist.** Only three
backends exist, and the most mature one, `WpfSurfaceHostService`, implements exactly 19
methods: `initialize`, `session/open`, `session/update`, `session/flush`,
`design/set-property`, `design/select`, `design/add-element`, `design/delete-elements`,
`design/rename`, `design/set-bounds`, `design/hit-test`, `design/theme`, `ping`, `shutdown`,
plus the WPF-only `design/add-menu-item`, `design/add-strip-item`, `design/move-element`,
`design/query-grid-guides`, `design/set-grid-track-size`.

`IDesignHostClient` names 11 of them in the shared `DesignerDocumentRpcClient`, which is why
`design/set-event` and `session/close` have authoritative shapes worth implementing even
though WPF does not implement them. The remaining interface methods
(`design/apply-layout`, `design/activate-default-event`) and `IDesignHostExport` /
`IDesignHostAppResources` are **implemented by no shipped backend**, so their `operation`
vocabulary and result shapes have no reference to copy. They are deliberately left
unimplemented rather than guessed: inventing an operation-name set that no caller agrees
with would fail at runtime exactly like a wrong `[JsonRpcMethod]` name, but with no compiler
to catch it. Add them when a caller needs them.

Authoritative wire shapes, copied from `WpfSurfaceHostService` + `WpfSurfaceHostClient`
(guessing these compiles but fails at runtime with `RemoteMethodNotFoundException`):

```
initialize        (string token, int protocolVersion, string sessionId)      -> HostHandshake
session/open      (DesignerDocumentSnapshot snapshot)                        -> DesignerSessionState
session/update    (DesignerDocumentSnapshot snapshot)                        -> DesignerSessionState
session/flush     (string sessionId, string documentId, long baseVersion)    -> DesignerEditSet
design/set-property (string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value) -> DesignerSessionState
design/reset-property (string sessionId, string documentId, long baseVersion, string elementId, string propertyName) -> DesignerSessionState
design/set-bounds (string sessionId, string documentId, long baseVersion, string elementId, double x, double y, double w, double h) -> DesignerSessionState
design/add-element (string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y) -> DesignerSessionState
design/delete-elements (string sessionId, string documentId, long baseVersion, string[] elementIds) -> DesignerSessionState
design/rename     (string sessionId, string documentId, long baseVersion, string elementId, string newName) -> DesignerSessionState
design/set-zorder (string sessionId, string documentId, long baseVersion, string elementId, bool bringToFront) -> DesignerSessionState
design/set-event  (string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName) -> DesignerSessionState
session/close     (string sessionId, string documentId)                      -> void
ping / shutdown   ()                                                        -> void
```

Element identity follows `DesignerElementNode`: `Id` is Core's `ElementId.Value`, and
`Path` is the child-index path from the root with the **root's own path being the empty
string** (a root `PickPath` of `""` is otherwise indistinguishable from "hit nothing").

## 6. Known traps

Recorded here because each is a documented failure mode in the OpenDevelop designer tree
that is invisible at compile time.

1. **Forwarding-wrapper trap.** If multiple documents are supported, the DDP methods are
   attached to a forwarding wrapper (cf. `MultiDocumentWpfSurfaceHostService`). A new
   `[JsonRpcMethod]` on the inner service then compiles clean and is discoverable by
   reflection, yet the real caller — StreamJsonRpc's target is the *wrapper* — throws
   `RemoteMethodNotFoundException`. Every added method needs a one-line forwarder.
2. **Addin SDK properties.** The addin must be `OpenDevelopAddin=true` with
   `OpenDevelopAddinKind=InProcess`. The child host must be `OpenDevelopAddinKind=OutOfProcessHost`,
   otherwise it is treated as in-process and trimmed. The SDK sets `UseAppHost=false` for
   the child itself. AnyCPU applies to the addin.
3. **Host version alignment.** Building the addin against an OpenDevelop newer than the
   installed host yields SD-1406. Point `OpenDevelopRoot` at the 5.5.8 worktree.
4. **`Link`ed Core sources and trimming.** If Core sources are linked into the trimmed
   child, reflection-based command dispatch must be preserved or the `IDocumentCommand`
   records need explicit trim descriptors.
5. **A child service that is the direct RPC target must declare `sessionId` and `documentId`
   on every method the client calls with them.** `DesignerDocumentRpcClient` puts both into the
   argument object of every document call, and **StreamJsonRpc resolves a target method by name
   *and* arity — extra named arguments are not dropped**:

   ```
   RemoteMethodNotFoundException: Unable to find method 'design/set-property/6' ...
     design/set-property(Int64, String, String, String)
     parameter(s) (excluding any CancellationToken): 4 - 4, but the request supplies 6
   ```

   This is worth writing down because the opposite is easy to assume and the assumption is
   invisible until a child is actually spawned. It also explains something that otherwise looks
   accidental: `WpfSurfaceHostService`'s methods declare only `(baseVersion, elementId, …)` and
   yet the same client works against them, because their RPC target is
   `MultiDocumentWpfSurfaceHostService`, whose forwarding methods carry the full signature
   including the two identity parameters. The wrapper is not merely the hand-maintained
   whitelist of trap #1 — it is also the identity-absorbing shim, and a single-document child
   that skips it must absorb the identity itself. `MauiDesignerHostService` therefore takes
   `string sessionId, string documentId` on every method the client sends them to, and
   `Require(sessionId, documentId)` rejects a call addressed to another document, so a child
   that outlived its document cannot mutate a stale one.
6. **`IXamlTypeResolver.IsView` means "is a renderable visual control", not "accepts UI
   children."** `DesignerXamlReader` rejects any nested element whose resolution has
   `IsView == false` with *"'Button' is not a visual control."* So `Button`, `Label`,
   `Entry` and every other control must resolve with `IsView: true`; `IsView: false` is only
   meaningful for a document root that merely *wraps* visual content. Content placement is
   driven by `ContentPropertyName` (`ContentView` → `Content`, `Label` → `Text`,
   `CollectionView` → `ItemsSource`, layouts → `null`).
7. **`XamlTypeResolution` is permissive, so resolution failures are silent.** The resolver
   returns `true` for unknown local names rather than `false`, so an unrecognised MAUI
   element becomes a synthetic `ControlTypeId` instead of a diagnostic. That is deliberate
   for a designer (third-party and user controls must still open), but it means a typo in a
   type name is never reported — only rendered as an unknown control.
8. **Test XAML must declare `xmlns:x`.** `x:Name` without
   `xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"` fails at
   `XDocument.Parse` with *"'x' is an undeclared prefix"* — a test-fixture bug that looks
   exactly like a resolver bug.

## 7. Open questions

- **Event handler shape.** OpenDevelop's `SetEventAsync` passes a handler name. MAUI
  uses code-behind (`Click="OnButtonClicked"`), whereas WPF/WinUI designers commonly emit
  a method reference. Whether the DDP surface expects a bare method name for MAUI needs to
  be pinned down before `design/set-event` is implemented.
- **`DesignerProtocol.Version`.** The host checks the child's reported version; the MAUI
  child must report `2`.
- **Multi-document vs single-document.** Determines whether trap #1 applies immediately.
  Single-document first is simpler.

## 8. Phases

Each phase ends with something verifiable. See the progress log in §9.

- [x] **Phase 0 — design** (this document).
- [x] **Phase 1 — DDP service, UI-free.** `MAUIDesigner.Ddp` + `MAUIDesigner.Ddp.Tests`.
      Verified on macOS: `dotnet test` → **31/31 pass**. Covers open, update, flush, close,
      set / reset property, set-event (including the flush round trip), markup-extension
      values, set-bounds, rename, add-element (including toolbox template children),
      delete-elements (including unknown ids), set-zorder, ping/shutdown and both
      `WaitForShutdown` release paths, a flush→reopen round trip, malformed-XAML rejection
      and token rejection.
- [x] **Phase 2 — in-process addin.** `ICSharpCode.MauiDesigner.AddIn` builds and deploys
      to `OpenDevelop/AddIns/DisplayBindings/MauiDesigner/` on macOS, 0 errors / 0 warnings.
      `MauiDesigner.addin` registers a Secondary `DisplayBinding`; `MauiDesignerDisplayBinding`
      claims a file by root namespace (falling back to project markers) and adopts CoreWF's
      already-attached guard; `MauiSurfaceHostClient` implements the whole client seam;
      `MauiDesignerViewContent` is a placeholder tab.
      `MAUIDesigner.Dialect` (+ tests) holds the claim logic at `net10.0` so it is testable
      here. **Still to do:** the real design surface control (frame rendering, adorners,
      pointer input).
- [x] **Phase 2b — file ownership in OpenDevelop.** The runtime dialect registry described in
      §5a. Verified live 2026-09-28: a MAUI page gets exactly `[AvalonEditViewContent,
      MauiDesignerViewContent]` (no WPF tab); a WPF page still gets `WpfViewContent`.
- [ ] **Phase 3 — platform boundary extraction.** Move direct `Windows.Foundation`,
      `Windows.System`, WinUI transform, pointer, keyboard, and custom-handler use out of
      shared designer code. Define host interfaces for input, coordinates, native view capture,
      and window preview. Move pure tests to `net10.0`; no UI behaviour may be duplicated only
      to make a platform test pass.
- [ ] **Phase 4 — native hosts.** Make the child host and standalone designer target both
      `net10.0-windows10.0.19041.0` and `net10.0-macos`. Windows uses the existing WinUI
      adapter; macOS bootstraps `NSApplication` through MAUI Labs'
      `Microsoft.Maui.Platforms.MacOS` / `MacOSMauiApplication` and supplies an AppKit adapter.
      The first milestone is open XAML → render a real MAUI frame → hit test, on each OS.
      **macOS milestone reached 2026-09-28** (open → real frame → real layout → hit test → edit
      re-render, end to end over loopback JSON-RPC and live in OpenDevelop). Windows host not started.
- [ ] **Phase 5 — addin lifecycle and interaction.** *Lifecycle half done (2026-09-28):*
      child start from `Host/`, open/update per load, flush-on-save, shutdown on dispose.
      Interaction (intent routing, hit-test/select/mutation) still to do. Replace the placeholder
      `MauiDesignerViewContent` flow with child startup, open/update, render, stale-frame
      handling, flush-on-save, disposal, and `MauiDesignSurface.IntentRequested` routing to
      hit-test/select/mutation. Deploy the correct host payload beside the addin; the host file
      name, addin manifest, and `OpenDevelopAddinKind=OutOfProcessHost` must agree.
- [ ] **Phase 6 — cross-platform verification.** Run the shared `net10.0` suites on both OSs.
      Add one native smoke/integration test per host: open MAUI XAML → non-empty render →
      set property → flush contains the update. Windows rendering is not a substitute for the
      AppKit test, and vice versa.

### Building

`MAUIDesigner.Ddp` and `ICSharpCode.MauiDesigner.AddIn` need a path to an OpenDevelop
checkout. Pass it as an MSBuild property or via the environment; there is deliberately no
machine-specific default in either csproj.

```bash
export OPENDEVELOP_ROOT=/Users/lextm/wpf-tools/OpenDevelop
export SDKROOT=$(xcrun --sdk macosx --show-sdk-path)   # macOS: the CLT 27 linker rejects its own SDK
dotnet test maui-designer-native/MAUIDesigner.Dialect.Tests/MAUIDesigner.Dialect.Tests.csproj
dotnet test maui-designer-native/MAUIDesigner.Ddp.Tests/MAUIDesigner.Ddp.Tests.csproj
dotnet test maui-designer-native/MAUIDesigner.Ddp.Host.Tests/MAUIDesigner.Ddp.Host.Tests.csproj   # spawns the real children
dotnet build maui-designer-native/ICSharpCode.MauiDesigner.AddIn/ICSharpCode.MauiDesigner.AddIn.csproj
```

The addin builds into **its own** `ICSharpCode.MauiDesigner.AddIn/bin/<Configuration>/` (with the
child hosts under `Host/`) and is never deployed into an OpenDevelop tree. It is loaded the way
CoreWF's WorkflowDesigner is: run the addin project from a hosting OpenDevelop; the Addin SDK
starts a second OpenDevelop with `-addindir:<that bin> -configdir:... -devflow:<port>`.

**Integration tests** live here too, in `ICSharpCode.MauiDesigner.IntegrationTests` (CoreWF's
`WorkflowDesigner.IntegrationTests` shape: it source-links OpenDevelop's `OpenDevelopAppFixture.cs`).
The parent OpenDevelop is `OPENDEVELOP_APP_PATH`; it opens the addin project, checks its
`StartArguments` carry `-addindir:` and `-devflow:9302` (9302 so CoreWF's 9301 can coexist), runs
it, and the journey drives the child on 9302. The host must be at least the OpenDevelop version the
addin was built against (SD-1406); the installed `/Applications/OpenDevelop.app` (5.5.11.0 on
2026-09-28) predates the dialect registry, so point it at a current build:

```bash
export OPENDEVELOP_APP_PATH=$OPENDEVELOP_ROOT/src/Main/SharpDevelop/bin/Debug/net10.0-windows/OpenDevelop
dotnet build maui-designer-native/ICSharpCode.MauiDesigner.IntegrationTests/ICSharpCode.MauiDesigner.IntegrationTests.csproj
dotnet run --project maui-designer-native/ICSharpCode.MauiDesigner.IntegrationTests/ICSharpCode.MauiDesigner.IntegrationTests.csproj --no-build
```

`$(OpenDevelopRoot)` is normalised with `EnsureTrailingSlash` and the existence check uses
forward slashes — a backslash there silently fails on macOS/Linux, because MSBuild
normalises separators in `ProjectReference` but **not** inside an `Exists()` condition.
The addin project needs `SDKROOT` pointed at a real SDK on this machine (the CLT 27
linker otherwise fails).

## 9. Progress log

### 2026-09-28

#### Cross-platform decision

- **Decision:** macOS support uses the MAUI Labs native AppKit backend, not Mac Catalyst.
  The macOS target is `net10.0-macos`; it uses `Microsoft.Maui.Platforms.MacOS` and the
  MAUI Labs `NSApplication` / `MacOSMauiApplication` bootstrap. Windows remains a native
  Windows host. Package versions will be pinned after the first successful macOS restore and
  native smoke run.
- The current machine has the `.NET 10` `macos` workload. It does not currently have a MAUI
  workload installed, so the new AppKit target cannot be compiled here until its package/tooling
  restore prerequisites are established. This does not block the existing `net10.0` suites.
- The portability inventory is explicit: `CanvasViewportView`, `ToolboxItemView`, and
  `SidebarResizeHandle`; WinUI calls in `ControlMaterializer`; and the direct
  `Windows.System.VirtualKey` paths in `MainPage.xaml.cs` are Windows adapters to extract.
  They are not evidence that the document model or DDP needs to fork.
- The existing addin WPF surface is built through LibreWPF and its geometry/protocol pieces are
  already exercised on macOS. The missing cross-platform work is the real MAUI child host and
  its view-content lifecycle, not a replacement for the OpenDevelop surface.
- The initial delivery order is deliberately vertical: (1) extract platform adapters and move
  pure tests below `net10.0`; (2) start/open/render/hit-test an AppKit child; (3) wire the
  addin lifecycle; (4) add property editing, resize, and richer native interactions. Do not
  port the full standalone workspace before a real addin render round trip exists.

- Read the DDP contract (`OpenDevelop/doc/technotes/designer-common.md`), `DesignerProtocol`,
  `IDesignHostClient`, `IDesignerChildService`, `DesignerChildHost.Run` and
  `DesignerHostProcessClient`.
- Surveyed `MAUIDesigner.Fresh.Core`: `DocumentSession` (undo/redo, `Execute`),
  `DocumentEditor` (`Add`/`Remove`/`Reparent`/`Reorder`/`SetProperty`/`SetBounds`),
  the `DocumentCommands` records, `DesignerXamlReader`/`Writer`, `DropTargetResolver`,
  `GridGeometry`. Confirmed a 1:1 fit with the DDP document methods (§5).
- Confirmed `Designer.Remote`/`Designer.Server` are `net9.0;net10.0` and UI-free, so the
  split in §4 is buildable and testable on macOS.
- Copied the `OpenDevelopRoot` + Addin SDK wiring pattern from CoreWF's
  `WorkflowDesigner.csproj` / `WorkflowDesigner.Host.csproj` / `WorkflowDesigner.Host.Tests.csproj`.
- **Decided:** dual transport. StreamJsonRpc (via `Designer.Remote`) is primary; the
  existing `IHostedDesignerBridge` / `NamedPipeHostedDesignerBridge` stays for standalone
  `MAUIDesigner.Fresh.App` debugging and is not part of the addin path.

### Phase 1

- Created `MAUIDesigner.Ddp` (net10.0) and `MAUIDesigner.Ddp.Tests`; both added to
  `MAUIDesigner.Fresh.slnx`.
- `MAUIDesigner.Ddp` references `MAUIDesigner.Fresh.Core` by `ProjectReference` and
  `Designer.Remote` with `Private="false"` (the host supplies
  `ICSharpCode.Designer.Remote`; copying it into the addin would risk a second,
  version-skewed copy). `StreamJsonRpc` pinned to 2.25.29 to match OpenDevelop's
  `Directory.Packages.props`, so the `[JsonRpcMethod]` attribute type identity matches.
- `MauiXamlTypeResolver` — table-driven MAUI type map (content property, visual property
  set, `IsView` always true), falling back to a synthetic `ControlTypeId` for unknown
  elements so third-party/user controls still open.
- `MauiSessionStateBuilder` — `DesignerDocument` → `DesignerSessionState`, including
  root-empty path semantics, `x:Name` extraction, and `DesignerValueKind` → DDP property
  `Kind` (`Literal`→`String`, markup/property-element/raw→`Xaml`).
- `MauiDesignerHostService` — 13 DDP methods, token-verified `initialize`, and
  `WaitForShutdown`/`OnParentDisconnected` so it satisfies `IDesignerChildService`.
  Failures are reported as `Accepted = false` + `Error` rather than thrown, except the
  handshake token, which must throw.
- **21/21 tests pass on macOS** (`dotnet test`, .NET SDK 10.0.201).
- Three defects found and fixed while getting there, all recorded in §6/§5: `DocumentEditor`
  is internal; `IsView` is not "accepts children"; test XAML needs `xmlns:x`. Two further
  test-only mistakes (asserting `DocumentId` against a file name, and inverted z-order
  expectations) were mine, not the implementation's.
- `design/set-event` and `design/apply-layout` are mapped in §5 but **not implemented yet**;
  `design/hit-test`/`render`/`export-png`/`theme`/`app-resources` belong to the Windows host
  (Phase 3).

### Phase 2

- **Investigated the out-of-tree registration question first**, because it decides whether
  OpenDevelop has to change at all. Findings are in §5a. Summary: registration already works
  (CoreWF does it from its own `.addin`), but *file ownership arbitration* does not, and that
  is the one genuine OpenDevelop gap.
- Created `ICSharpCode.MauiDesigner.AddIn` (InProcess, `net10.0-windows`, `LibreWPF.Sdk`).
  It builds and deploys to `AddIns/DisplayBindings/MauiDesigner/` on macOS.
- Added `maui-designer-native/global.json` and `NuGet.config`; without them the addin cannot
  resolve `LibreWPF.Sdk` at all (MSB4236). See "Build entry point".
- `MAUIDesigner.Dialect` (`net10.0`) holds the file-ownership evidence, deliberately below
  `net10.0-windows` because a `net10.0-windows` test project cannot run on macOS at all.
  **19/19 tests pass**, including WPF/WinUI projects being correctly *not* matched, markup
  winning over a misleading project, and project evidence working when markup is not
  available yet.
- `MauiDesignerDisplayBinding` reads the root element's namespace and defers to the dialect
  matcher; adopts CoreWF's "already attached → return no secondary content" guard.
- `MauiDesignerViewContent` is a placeholder (`UserContent`, not `Control` — the base class
  seals `Control` and exposes a `ContentPresenter` through `UserContent`).
- Added `MauiSurfaceHostClient` (`net10.0-windows`, builds and deploys clean). It implements
  `IDesignHostClient` + `IDesignHostExport` + `IDesignHostAppResources` + `IEventBindingHost`
  over a nested `Connection : DesignerHostProcessClient`, forwarding the 11 calls that
  `DesignerDocumentRpcClient` already names to it and issuing the remaining ones itself with
  `sessionId`/`documentId` merged into every argument object.
- **Service/client gap to close in Phase 3.** The proxy can now send these methods, but
  `MauiDesignerHostService` does not implement all of them yet:
  - implemented: `design/set-property`, `reset-property`, `set-bounds`, `add-element`,
    `delete-elements`, `rename`, `set-zorder`, `set-event`, `session/close`, `ping`,
    `shutdown`
  - deliberately **not** implemented, no reference to copy: `design/apply-layout`,
    `design/activate-default-event` (no shipped backend has them)
  - belongs to Phase 3 (canvas-bound): `design/hit-test`, `design/render`,
    `design/export-png`, `design/theme`, `design/app-resources`, `design/select`
- `BindEvent` deliberately throws `NotSupportedException` pending the §7 decision on MAUI's
  code-behind event shape, rather than writing a binding that would be wrong.
- Pooling/recovery (`SharedDesignerHostPool`, `SharedDesignerHostRecovery`) is deliberately
  **not** used yet; `WpfSurfaceHostClient` uses it, and MAUI should adopt it once the child
  host exists and its restart behaviour is worth preserving.
- Two bugs of mine, both caught by tests: I grouped `PropertyGroup` *elements* instead of
  their children when detecting `UseMaui` (copied `XamlFrameworkDetector`'s shape but not its
  predicate), and I first wrote `ReadRootElement` with a broken static-mutable-property hack.
- **Not yet done:** the design surface control, and the child host itself.

### End-to-end verification (real child, real loopback JSON-RPC)

- Split `MauiSurfaceHostClient` out of the addin into `MAUIDesigner.Ddp.Client` (net10.0). It
  only ever needed `Designer.Remote`, so keeping it in the `net10.0-windows` addin made it
  untestable on macOS for no reason — the same reason `Designer.Remote` itself is net10.0.
- Added `MAUIDesigner.Ddp.Host` (net10.0, Exe): `DesignerChildHost.Run` with
  `MauiDesignerHostService`. This is the Phase 3 skeleton minus the canvas.
- Added `MAUIDesigner.Ddp.Host.Tests`, modelled on the OpenDevelop tree's own
  `WpfSurfaceHostRpcTests`: it spawns the real child and drives it with the real client, so
  wire-level mistakes become test failures instead of production ones. **5/5 pass.**
  A spawned child is a separate application, so unlike an in-process addin it cannot borrow
  `Designer.Remote`/`Designer.Server` from the host — those references must keep `Private=true`
  or the child dies at startup on its own bootstrap assembly.
- **This suite found a real defect and disproved a claim in this document.** The shared
  `DesignerDocumentRpcClient` sends `sessionId` and `documentId` on every document call, and
  StreamJsonRpc resolves by name *and arity*, so a direct-target service that omitted them
  failed with `design/set-property/6 … 4 - 4, but the request supplies 6`. §6 trap 5 previously
  asserted the opposite and has been corrected. `MauiDesignerHostService` now declares the
  identity on every such method and `Require(sessionId, documentId)` rejects a call addressed to
  another document.
- A mutation against an unknown element returns a state with `Accepted = false` rather than
  throwing: over the wire a throw reads as a transport failure instead of a rejected edit.

### Phase 2b / Phase 5 lifecycle (2026-09-28)

- **The dialect was never registered, for two independent reasons**, which together left every
  MAUI page with a second (WPF) Design tab:
  1. `MauiDesigner.addin` used `/SharpDevelop/AutostartAfterWorkbenchInitialized`. The real path
     is `/SharpDevelop/Workbench/AutostartAfterWorkbenchInitialized` (`CallHelper.cs`); a wrong
     path is simply never built, so this fails with **no error at all**. The tell was that
     "Loading addin MAUI Designer" appeared only when the file opened, never at startup.
     CoreWF's `.addin` had the same mistake (plus `RegisterWorkflowDialect` on the too-early
     `/SharpDevelop/Autostart`); fixed there too. Stride's was already correct.
  2. The registry matcher built `new MauiXamlProbe { FileName = fileName }` only - with no root
     namespace or project, `MauiXamlDialect.Matches` can never be true. It now uses
     `MauiDesignerDisplayBinding.ProbeFile`, which reads the root element and owning project.
- **Correction:** the path *does* call `Run()` (`command.Execute(null)`). The earlier claim that
  it only builds codons was wrong; `RegisterMauiDialect`/`RegisterWorkflowDialect` now register
  in `Run()`.
- `MauiDesignerViewContent` now owns the child lifecycle over `MauiSurfaceHostClient`. It prefers
  `Host/MAUIDesigner.Host.dll` (native, Phase 4) and falls back to `Host/MAUIDesigner.Ddp.Host.dll`
  (UI-free) so the document round trip works before rendering exists. The addin build deploys the
  Ddp host there (`DeployDesignHost` target). Verified: the child process starts from `Host/`
  when the Design view activates and exits with the IDE.
- `ICSharpCode.MauiDesigner.AddIn.Tests` failed 5/6 on macOS with `DllNotFoundException
  wpfgfx_cor3.dll`: the test process had no LibreWPF portable bootstrap. Added
  `ProGpuWpfEnablePortableBootstrap` (as `SharpDevelop.csproj` does) → 5/6. The remaining failure
  asserts `surface.IsVisible`, which is false for an element never hosted in a window.
- Building this addin rebuilds OpenDevelop's referenced projects **without** pinned GitVersion
  values; after an OpenDevelop commit that produced a 5.5.12.4 addin against a 5.5.12.3 shell and
  `Cannot find class: MauiDesignerDisplayBinding`. Rebuild OpenDevelop first (its integration test
  project) whenever its revision has moved.


### Phase 4 spike: AppKit render path (2026-09-28)

Throwaway spike (not committed), `net10.0-macos` + `Microsoft.Maui.Controls` 10.0.41 +
`Microsoft.Maui.Platforms.MacOS` `0.1.0-preview.12.26421.1`. Every risky question is answered:

| Question | Answer (measured) |
|---|---|
| Is the MAUI workload needed? | **No.** `UseMaui=true` demands a workload (the SDK asks for `maui-tizen`); with `UseMaui=false` and plain package references the project builds against only the installed `macos` workload. |
| Can the child be started with `dotnet exec host.dll`? | **No.** `NSApplication.Init` fails in `xamarin_initialize` (`dlopen lib__Internal`): an AppKit app needs its bundle's native launcher. Start `Host.app/Contents/MacOS/Host` instead — `DesignerHostProcessClient` already allows this by overriding `DotnetHostPath` (the bundle executable) and `BuildCommandLine` (`--port N --token T` only). No OpenDevelop change needed. |
| Runtime XAML without `x:Class`? | **Yes.** `Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(page, xaml)` on a bare `ContentPage`. |
| Render without showing anything? | **Yes.** `ElementExtensions.ToPlatform(page, mauiContext)` (returns `object` because Maui.Core has no macos build — cast to `NSView`), put it in a borderless `NSWindow` that is never ordered front, `Measure`/`Arrange` the page, then `BitmapImageRepForCachingDisplayInRect` + `CacheDisplay` → PNG. `visibleWindows=0`. MAUI's own app window is hidden with `OrderOut` in `Window.Created`; `Window.X/Y` are ignored. |
| Frame scale | 2x on a Retina display (800x600 px for a 400x300 page) — report `Width/Height` in design units, pixels separately. |
| Geometry for hit-test/adorners | `VisualElement.Frame` is in design units and matches the pixels (`Go` at 20,58 360x24). |
| No Dock icon / focus stealing | `NSApplication.SharedApplication.ActivationPolicy = Accessory`. |
| API trap | `NSApplication.Windows` is `DangerousWindows` (an `NSArray<NSWindow>`; call `ToArray()`). |

Next: `MAUIDesigner.Host` (`net10.0-macos`, bundle) = `DesignerChildHost.Run` on a background
thread + `MauiDesignerHostService` for the document methods, with `design/render` and
`design/hit-test` marshalled to the AppKit main thread using the recipe above; the client
launches the bundle executable.

### Phase 4: native macOS host (2026-09-28)

- `MAUIDesigner.Host` (`net10.0-macos`, app bundle `MAUIDesigner.Host.app`). AppKit owns the main
  thread (`NSApplication.Main`); `DesignerChildHost.Run` runs on a worker thread with the main
  thread's `SynchronizationContext` as `rpcSynchronizationContext`, so every RPC executes where MAUI
  objects may be touched. The connection starts from the hidden shell window's `Created` event,
  which is also where the `MauiContext` comes from.
- `AppKitMauiRenderer : IMauiDesignRenderer` - resolves the root's own type, strips `x:Class`/
  `x:Subclass` and event attributes (runtime XAML has no code-behind; MAUI rejects them), calls
  `LoadFromXaml`, renders into a never-shown borderless window at a 400x700 design canvas, and
  reports per-path bounds by walking the realized tree (layout children, `Content` of
  page/ContentView/Border/ScrollView), accumulating parent-relative `Frame`s.
- `MAUIDesigner.Ddp` stays UI-free: `IMauiDesignRenderer` + `MauiRenderResult`. The service renders
  every **accepted** state (never a rejected one), copies bounds onto the tree, uses a strictly
  increasing frame sequence, turns a renderer failure into a Warning diagnostic without rejecting the
  edit, and now implements `design/hit-test` from the rendered bounds (innermost hit, named chain
  innermost-first, `Hit = true` for the root). 8 new tests with a fake renderer → **39/39**.
- The client starts a native executable directly (`DotnetHostPath` = the bundle executable,
  arguments `--port N --token T`); `.dll` hosts keep `dotnet exec`.
- The addin builds the native host on macOS and deploys the whole bundle with `ditto` (a bundle
  carries symlinks/exec bits `<Copy>` loses) to `Host/MAUIDesigner.Host.app`; `LocateHost` prefers
  it on macOS and falls back to the model-only `Host/MAUIDesigner.Ddp.Host.dll`.
- `NativeMacHostRenderTests` spawns the real bundle: PNG signature and size, `Title`/`Action` with
  distinct non-empty rectangles in stack order, hit-test on `Action`, edit → newer frame. Set
  `OPENDEVELOP_MAUI_DUMP_PNG=<file>` to look at the frame. Returns early off macOS (xunit 2 has no
  runtime skip).
- **Two surface bugs fixed** in `MauiDesignSurface`: the design size was read back off the viewport
  it is fitted *from* (so it was zero forever), and the bitmap was drawn at 0,0 unscaled while the
  overlays used the fitted viewport. The image is now sized/offset from the viewport (`Stretch.Fill`).
  The renderer sets the rep's point size so the PNG carries 144 DPI and WPF shows it at design size.
- The designer now writes to its own **"MAUI Designer"** Output channel (host start/ready/exit,
  per-load element count and frame size/time, rejections, render diagnostics, child stderr).
- `od.maui-designer.status` DevFlow action (marshals to the UI thread itself: an addin action is not
  guaranteed it). Verified live: native host from `Host/`, accepted, 4 elements, frame 400x700 @2x,
  ~185 ms, `surface.hasImage = true`.
- Still to do: surface interaction (click → hit-test → select → property edits), a design-size
  choice instead of the fixed 400x700, and the Windows (WinUI) native host.

### IDE feature parity, first group (2026-09-28)

Modelled on the LibreWPF designer's IDE side (inventory of its ~33 features in this session), on
option A: the shared canvas rather than the bespoke `MauiDesignSurface`.

- **Surface = the shared `UnoDesignSurfaceControl`** (the WinUI/Uno designer's canvas: `DesignerCanvas`
  toolbar, zoom/fit, selection adorner, 8 handles, drag, context menu), source-linked from OpenDevelop
  because it is a pure view with no protocol code. `MauiDesignSurface`/`MAUIDesigner.Surface` were
  deleted on 2026-09-29 (nothing referenced them any more).
- **Frames are BGRA32**, not PNG: the shared surface decodes with managed code because WPF's PNG
  decoder is a native WIC codec LibreWPF lacks. `Width/Height` are pixels, `Dpi` the scale; the
  AppKit renderer redraws the capture into premultiplied BGRA (row 0 = top — the native test checks
  the button's blue at its reported centre, which catches a flipped frame).
- **Working in the IDE (verified live and by the integration test):** click → child hit-test →
  select (Ctrl toggles multi-select), synced to the Document Outline (both ways) and the Properties
  pad; Properties pad edits, including properties the XAML does not set yet (the child now lists
  each type's known properties, unset ones as `IsNull`); Toolbox (`.NET MAUI` scope) → drop into the
  nearest container, unique `x:Name`, selected; drag-move/resize; Delete key + context-menu Delete,
  Bring to Front/Send to Back; undo/redo via the child's own document history (`design/undo`,
  `design/redo`); dirty tracking + flush on save; Errors pad from diagnostics; Output channel.
- **Layout-aware set-bounds** (in the UI-free service, unit-tested): only an `AbsoluteLayout` gets
  `AbsoluteLayout.LayoutBounds`; elsewhere a resize becomes `WidthRequest`/`HeightRequest` and a move
  in a stack a reorder to the drop position. Fixed with it: `design/add-element` used to give every
  new element `LayoutBounds="x,y,0,0"`, meaningless in a stack.
- DevFlow: `od.maui-designer.status|select|click|properties-pad.edit|toolbox.drop|move-resize|
  delete|undo|redo` — each through the UI's own path.
- **Not yet:** the Source tab's toolbox is still chosen by OpenDevelop's hard-coded WPF/WinUI branch
  (`AvalonEditViewContent.ToolsContent`), so dragging a MAUI item onto the XAML editor needs an
  OpenDevelop change; copy/paste/wrap; inline text edit; design-size presets; theme; Windows host.
  The first designer save re-emits attributes in the writer's order (a formatting diff).


### Licence split (2026-09-28)

This repository is GPL-3.0. The OpenDevelop addin is now MIT, in `../opendevelop-addin` (its own
`LICENSE`, `README.md`, `MauiDesigner.OpenDevelop.slnx`): the addin, `MAUIDesigner.Ddp.Client`,
`MAUIDesigner.Dialect` and the integration tests moved there. What stays here is the DDP host side:
`MAUIDesigner.Fresh.Core`, `MAUIDesigner.Ddp` (+ tests), `MAUIDesigner.Ddp.Host`, `MAUIDesigner.Host`
(+ `MAUIDesigner.Ddp.Host.Tests`, which may use the MIT client). The addin uses no GPL code: the
toolbox catalog now comes from the host over DDP (`design/capabilities`, the `DesignerCapabilities`
DTO), and the GPL hosts are bundled beside the addin as separate programs (`BundleDesignHosts`),
not referenced. `LicenseBoundaryTests` (MIT side) enforces it. `MAUIDesigner.Surface` is no longer
used by the addin.

The design canvas was also rebuilt on OpenDevelop's shared presentation (`MauiDesignCanvas`:
`DesignerCanvas` + `DesignViewport` + `DesignFramePresenter` + `SelectionAdornerLayer` +
`GridlineOverlay`, the WPF designer's composition) instead of borrowing the WinUI designer's canvas.
That one expressed "100%" relative to Fit and fitted against the ScrollViewer's viewport, so a tall
page oscillated between two scales as the scrollbars came and went (measured 1.0/0.909). Now 100% is
an absolute scale and the viewport is computed from the host's own size: stable (8/8 samples).

### Host seam per the DDP contract (2026-09-28)

- The view content now depends only on `IDesignHostClient` plus capabilities feature-detected per
  call (designer-common.md, "The host-side adapter seam"); the concrete `MauiSurfaceHostClient`
  appears only where the host is started. The client declares exactly what the host implements:
  `IDesignHostPropertyReset`, `IDesignHostEventBinding`, `IDesignHostBounds`, `IDesignHostHitTesting`,
  plus the host-specific `IDesignHostCapabilities` (toolbox catalog). It no longer claims
  `IDesignHostExport`/`IDesignHostAppResources`/`IEventBindingHost` (the host has no export-png or
  app-resources, and `BindEvent` threw). Z-order lives in `IDesignHostLayout` together with
  `ApplyLayout`, which the host does not implement, so MAUI does not claim it and the canvas has no
  Bring to Front/Send to Back; an unsupported capability returns a rejected state, never throws.
- Undo/redo is IDE-side, as in the WPF designer: each designer edit first flushes the child's XAML
  and pushes it; undo/redo restore a snapshot with `session/update`. The non-contract
  `design/undo`/`design/redo` were removed from the host and client.
- Found by the integration test while doing it: work run on the pool must not read the view
  content's own properties (`PrimaryFileName` is UI-thread-bound) - the flush helper now takes the
  file name; and a synchronous Properties-pad edit's deferred apply is tracked as `Loaded` so a
  status read waits for it.

### Missing features, second group (2026-09-28)

All exercised by the MIT integration journey (1/1) plus unit/native tests.

- **Copy/cut/paste.** Host: `design/copy-elements` returns each subtree as self-contained XAML;
  paste is `design/add-element` with that XAML as the item's Template. `design/add-element` now
  renames x:Names that already exist (Title -> Title2) and gives the pasted nodes fresh ids - the
  reader derives a named element's id from its x:Name, so a copy collided with its original by id.
  IDE: `IClipboardHandler`, context menu and Ctrl+X/C/V; clipboard format
  `OpenDevelop.MauiDesigner.Xaml` with an in-process fallback. Capability `IDesignHostClipboard`.
- **Inline text edit.** Double-click an element with a literal `Text`: a TextBox over it on the
  canvas; Enter/focus loss commits `design/set-property Text`, Escape cancels; bound text
  (`{Binding}`) and non-text elements get no editor.
- **Design-size presets.** Host: `design/set-design-size` (presentation only: no version bump, not
  dirty, not undo); default is now Phone 390x844. The canvas shows the shared design-size combo
  (Phone/Tablet/Desktop). Capability `IDesignHostDesignSize`.
- **Theme.** Host: `design/theme` (the shared `IDesignHostTheme` contract, WPF's wire shape) sets
  MAUI's `Application.UserAppTheme`; states report `DesignThemes = [Light, Dark]`, which is what
  makes the canvas show its theme combo. Found on real pixels: a page with no background renders
  TRANSPARENT (in an app the window provides the colour), so the renderer now paints the theme's
  default page colour (White / #1C1C1E) when the page sets none. MAUI's own theme-aware defaults
  (label text) follow `UserAppTheme` by themselves.
- **Toolbox on the XAML source editor.** OpenDevelop change (MIT): `XamlDialectRegistration.ToolsContent`
  + `XamlDialectRegistry.GetToolsContent`, consulted by `AvalonEditViewContent.ToolsContent` before
  its built-in WPF/WinUI branch; the MAUI registration supplies its toolbox. Limitation: the MAUI
  toolbox catalog comes from the host, so until a Design view has connected once in the session the
  Source tab's MAUI toolbox holds only the Pointer.
