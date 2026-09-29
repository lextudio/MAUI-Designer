# .NET MAUI designer addin for OpenDevelop (MIT)

This folder is **MIT-licensed** (see `LICENSE`). The rest of this repository, in particular
`../maui-designer-native`, is **GPL-3.0**.

| Part | Licence | What it is |
|---|---|---|
| `ICSharpCode.MauiDesigner.AddIn` | MIT | The in-process OpenDevelop addin: display binding, view content, the design canvas (built on OpenDevelop's shared designer presentation), Properties/Outline/Toolbox integration, DevFlow actions |
| `MAUIDesigner.Ddp.Client` | MIT | The DDP client that starts and talks to the design host |
| `MAUIDesigner.Dialect` | MIT | "Is this .xaml file MAUI?" (markup/project evidence) |
| `ICSharpCode.MauiDesigner.IntegrationTests` | MIT | The addin + real host, driven inside OpenDevelop |
| `../maui-designer-native/MAUIDesigner.Host`, `MAUIDesigner.Ddp.Host`, `MAUIDesigner.Ddp`, `MAUIDesigner.Fresh.Core` | GPL-3.0 | The DDP design host apps and the document model behind them |

**The boundary.** Nothing here references, links or source-includes anything from
`../maui-designer-native`. The addin talks to the design host only over the Designer Protocol
(DDP, JSON-RPC over a loopback socket, DTOs from OpenDevelop's MIT `Designer.Remote`); everything
MAUI-specific - rendering, element layout, the property catalog, the toolbox - comes from the host
over DDP. `LicenseBoundaryTests` enforces this: it fails on any MIT project item pointing into the
GPL tree and on any GPL assembly referenced by the built addin.

**Bundling.** The GPL host apps are built on their own and copied, as separate programs, into the
addin's `Host/` folder (`BundleDesignHosts` target; override `MauiDesignHostRoot`,
`MauiModelHostOutput` or `MauiNativeMacHostBundle`). They run as child processes; nothing is linked.

## Build and test (macOS)

```bash
export OPENDEVELOP_ROOT=/path/to/OpenDevelop
export SDKROOT=$(xcrun --sdk macosx --show-sdk-path)       # the CLT 27 linker rejects its own SDK
# 1. the GPL hosts
dotnet build ../maui-designer-native/MAUIDesigner.Ddp.Host/MAUIDesigner.Ddp.Host.csproj
dotnet build ../maui-designer-native/MAUIDesigner.Host/MAUIDesigner.Host.csproj
# 2. the MIT addin (bundles the hosts into bin/Debug/Host)
dotnet build ICSharpCode.MauiDesigner.AddIn/ICSharpCode.MauiDesigner.AddIn.csproj
dotnet test ICSharpCode.MauiDesigner.AddIn.Tests/ICSharpCode.MauiDesigner.AddIn.Tests.csproj
dotnet test MAUIDesigner.Dialect.Tests/MAUIDesigner.Dialect.Tests.csproj
# 3. integration: a parent OpenDevelop runs the addin project; the Addin SDK starts a second
#    OpenDevelop with -addindir:<bin> -devflow:9302 (CoreWF's pattern). The host must be at least
#    the OpenDevelop version the addin was built against.
export OPENDEVELOP_APP_PATH=$OPENDEVELOP_ROOT/src/Main/SharpDevelop/bin/Debug/net10.0-windows/OpenDevelop
dotnet build ICSharpCode.MauiDesigner.IntegrationTests/ICSharpCode.MauiDesigner.IntegrationTests.csproj
dotnet run --project ICSharpCode.MauiDesigner.IntegrationTests/ICSharpCode.MauiDesigner.IntegrationTests.csproj --no-build
```

The GPL side's design notes and progress log are in `../maui-designer-native/OPENDEVELOP-ADDIN.md`.
