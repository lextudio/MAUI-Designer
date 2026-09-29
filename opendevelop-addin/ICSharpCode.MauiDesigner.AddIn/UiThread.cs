using System.Runtime.CompilerServices;

using ICSharpCode.SharpDevelop;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// <c>await UiThread.Switch();</c> - continue on the UI thread. An await on the child returns on
/// whatever context was current when the operation started, and that is NOT reliably the UI
/// thread: a DevFlow action runs off it, and under LibreWPF a callback marshalled with
/// InvokeIfRequired does not install a synchronization context either. Touching WPF from the pool
/// then fails - fatally once a control creates a native window (a ComboBox popup: "NSWindow should
/// only be instantiated on the main thread!"). So every continuation that touches the UI switches
/// explicitly instead of relying on a captured context.
/// </summary>
readonly struct UiThread : INotifyCompletion
{
    public static UiThread Switch() => default;

    public UiThread GetAwaiter() => this;

    public bool IsCompleted => !SD.MainThread.InvokeRequired;

    public void OnCompleted(Action continuation) => SD.MainThread.InvokeAsyncAndForget(continuation);

    public void GetResult() { }
}
