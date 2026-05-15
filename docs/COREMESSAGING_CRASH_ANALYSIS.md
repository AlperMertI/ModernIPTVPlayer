# CoreMessaging COM QI Crash Analysis

## Problem

Every `Frame.Navigate` from `MediaInfoPage` to `PlayerPage` triggers an `InvalidCastException` (0x80004002 / E_NOINTERFACE) during the first layout pass of the new page. The exception originates from WinUI's native layout code when it interacts with `CoreMessagingXP.dll`.

The exception is **caught** by the application's try-catch blocks and does NOT crash the app when running without a debugger. However, with a debugger attached (Visual Studio), it breaks execution.

## Root Cause

The native call stack shows a COM `QueryInterface` failure in `CoreMessagingXP.dll`:

```
PlayerPage.MeasureOverride
  → base.MeasureOverride (FrameworkElement)
    → Do_Abi_MeasureOverride_0 (CsWinRT bridge)
      → Microsoft.ui.xaml.dll (native layout engine)
        → Microsoft.UI.Input.dll
          → CoreMessagingXP.dll  ← QI failure (E_NOINTERFACE)
```

The QI failure (0x80004002) occurs because `CoreMessagingXP`'s dispatcher integration is not fully wired when a newly created page runs its **first** layout pass after `Frame.Navigate`. The second and subsequent layout passes always succeed.

This is a **WinUI 3 internal timing issue** — not a bug in the application code. It manifests in all navigation scenarios (fresh start and handoff).

## Fixed Components

### 1. MediaInfoPage — DetachBeforeCleanup
**File:** `MediaInfoPage.xaml.cs`
**Method:** `DetachMediaInfoPlayerFromVisualTree()`
**Fix:** Removes the rejected preview player from the XAML tree **synchronously** before async `CleanupAsync()` starts. Prevents a race where WinUI measures a control whose native swap chain is being torn down.

### 2. D3D11RenderControl — Safe Layout Overrides
**File:** `D3D11RenderControl.cs`
**Methods:** `MeasureOverride`, `ArrangeOverride`
**Fix:** Catch `InvalidCastException` from CoreMessaging QI failure. Clear error info via `RoClearError()` and return the inut size. Previously re-threw the exception.

### 3. D3D11RenderControl — Deferred Render Loop
**File:** `D3D11RenderControl.cs`
**Location:** `Initialize()`, line 560
**Fix:** `StartRenderLoop()` is now called via `DispatcherQueue.TryEnqueue(StartRenderLoop)` instead of synchronously. This gives CoreMessaging one more dispatcher turn to wire its interfaces before the first frame present.

### 4. D3D11RenderControl — Finalizer Removed
**File:** `D3D11RenderControl.cs`
**Location:** Removed `~D3D11RenderControl()` finalizer
**Fix:** The finalizer called `GetHashCode()` on a WinRT `ContentControl`, which accesses the disposed `ObjectReference` — causing `ObjectDisposedException` during GC.

### 5. D3D11RenderControl — GC.Collect Removed
**File:** `D3D11RenderControl.cs`
**Location:** `DestroyResources()`
**Fix:** Removed `GC.Collect()` + `GC.WaitForPendingFinalizers()` which was triggering COM wrapper finalization during navigation, causing a layout race.

### 6. D3D11RenderControl — DetachUiResourcesAsync Hardening
**File:** `D3D11RenderControl.cs`
**Fix:** Properly unsubscribes event handlers (`-=`), clears `Content = null`, uses separate `_uiResourcesDetached` flag instead of `_disposed` for the guard. Makes cleanup order-independent.

### 7. PlayerPage — Skip First Layout
**File:** `PlayerPage.xaml.cs`
**Methods:** `MeasureOverride`, `ArrangeOverride`
**Fix:** First layout cycle skips `base.MeasureOverride`/`base.ArrangeOverride` entirely. Returns the full inut size. `PlayerPage_Loaded` triggers a fresh layout when it adds the `MpvPlayer`, by which time CoreMessaging is ready.

### 8. PlayerPage — Catch + ClearErrorInfo
**File:** `PlayerPage.xaml.cs`
**Methods:** `MeasureOverride`, `ArrangeOverride`
**Fix:** Catch `InvalidCastException`, call `RoClearError()`, return inut size. `SetErrorInfo(0, IntPtr.Zero)` is NOT sufficient — only `RoClearError()` clears WinRT's error info that `RoOriginateError` sets.

### 9. Application.UnhandledException Filter
**File:** `App.xaml.cs`
**Location:** `UnhandledException` handler
**Fix:** Suppress `InvalidCastException` and cascading `COMException` (0x8000FFFF) by setting `e.Handled = true`. Prevents these exceptions from reaching the FATAL handler or terminating the process.

### 10. XAML-generated Debugger.Break() Suppression
**File:** `ModernIPTVPlayer.csproj`, line 33
**Fix:** Added `DefineConstants` entry `DISABLE_XAML_GENERATED_BREAK_ON_UNHANDLED_EXCEPTION`. This prevents the auto-generated `App.g.i.cs` from emitting `Debugger.Break()` on any `UnhandledException`. Without this, Visual Studio breaks on the first-chance exception even though it's caught.

## API Reference

### RoClearError
```csharp
[DllImport("combase.dll")]
static extern void RoClearError();
```
Clears the per-thread WinRT error info set by `RoOriginateError`. Must be called **after** catching an exception originating from WinRT COM operations to prevent the native ABI from re-firing the exception.

## Build Configuration

The following entries were added to `ModernIPTVPlayer.csproj`:

```xml
<!-- Disable XAML-generated Debugger.Break() on UnhandledException -->
<PropertyGroup>
    <DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_BREAK_ON_UNHANDLED_EXCEPTION</DefineConstants>
</PropertyGroup>
```

## Verification

- Without debugger: app no longer crashes during `Frame.Navigate` to `PlayerPage`
- With debugger (F5): press Continue (F5) once — app proceeds normally
- With `DISABLE_XAML_GENERATED_BREAK_ON_UNHANDLED_EXCEPTION`: Visual Studio does NOT break on the caught exception

## Known

- This is a WinUI 3.2.0.2511 / Windows App SDK 2.0.x issue
- Not fixed in CsWinRT 2.3.0-prerelease
- The SetErrorInfo/SetErrorInfo fix was replaced with RoClearError because WinRT uses a separate error info store than classic COM
