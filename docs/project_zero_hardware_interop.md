# ModernIPTVPlayer: Project Zero - Native Hardware Interop Documentation

## 1. Introduction
"Project Zero" was a stabilization effort focused on 4K HEVC hardware decoding performance and reliability when using the modern Libplacebo renderer (`gpu-next`) in ModernIPTVPlayer.

## 2. The Problem: Interop Identity Crisis
Previously, hardware decoding in `gpu-next` (Libplacebo backed by D3D11) suffered from "Identity Mismatches." The hardware decoder expected a native D3D11 driver (`ra_d3d11`), but received a Libplacebo abstraction (`ra_pl`). 

Attempts to "spoof" the identity resulted in:
- **`E_INVALIDARG` (0x80070057)**: SRV parameters failing because Libplacebo's internal validation didn't align with the decoder's planar texture expectations.
- **`AccessViolationException`**: Memory corruption during texture mapping.
- **Performance Drops**: Fallback to copy-modes adding 5-10ms overhead per 4K frame.

## 3. The Solution: Native Modern Interop
We moved away from "Identity Hacks" and implemented a **Native Interop Pipeline** built directly into the Libplacebo RA layer.

### 3.1. Unified D3D11 Context
The interop relies on sharing a single D3D11 device between the Decoder and the Renderer.
- **Enabled via `Player.cs`**: `gpu-api=d3d11` and `gpu-context=d3d11` must be set before initialization.
- **Video Support**: The D3D11 device is created with `0x800` (`D3D11_CREATE_DEVICE_VIDEO_SUPPORT`) to allow `d3d11va` to bind.

### 3.2. Poly-Mapping (The Bridge)
We updated `hwdec_d3d11va.c` to handle both native and Libplacebo renderers polymorphically.
- **Legacy Path**: Uses `ra_d3d11_wrap_tex_video` for `vo=gpu`.
- **Modern Path**: Uses `ra_pl_wrap_d3d11_video` for `vo=gpu-next`.

## 4. Implementation Details

### 4.1. Plane-Based Format Mapping
Hardware decoded surfaces (NV12/P010) are planar. Wrapping them requires distinct Shader Resource Views (SRV) for each plane. 
Our logic explicitly routes these in `hwdec_d3d11va.c`:
- **NV12**: 
  - Plane 0 (Luma) -> `DXGI_FORMAT_R8_UNORM`
  - Plane 1 (Chroma) -> `DXGI_FORMAT_R8G8_UNORM`
- **P010**:
  - Plane 0 (Luma) -> `DXGI_FORMAT_R16_UNORM`
  - Plane 1 (Chroma) -> `DXGI_FORMAT_R16G16_UNORM`

### 4.2. Libplacebo Native Wrapper
Implementation: `ra_pl_wrap_d3d11_video` calls Libplacebo's native `pl_d3d11_wrap`.
This allows Libplacebo to manage the resource views internally using state-of-the-art gpu-next logic, ensuring that `blit_src` and `blit_dst` capabilities are correctly configured (essential for HDR/SDR mixing and OSD rendering).

## 5. Affected Files Registry

| Component | File Path | Responsibility |
| :--- | :--- | :--- |
| **Renderer (C)** | `ra_pl.c / .h` | Implements `ra_pl_wrap_d3d11_video` using `pl_d3d11_wrap`. |
| **Renderer (C)** | `ra_d3d11.c` | Polymorphic device/context extraction helper. |
| **Decoder (C)** | `hwdec_d3d11va.c` | Controls the mapping logic and routes textures based on RA type. |
| **Core (C#)** | `Player.cs` | Manages unified D3D11 context creation and initialization. |
| **Logic (C#)** | `MpvSetupHelper.cs` | Translates UI settings (AutoSafe/AutoCopy) into correct interop parameters. |

## 6. Performance Benchmarks
Post-stabilization metrics for 4K HEVC @ 60fps:
- **Total Frame Time**: **~0.9ms - 1.2ms** (Previous: 5ms+)
- **Memory Overhead**: 100MB+ reduction in VRAM due to elimination of redundant staging textures.
- **Reliability**: ZERO instances of `E_INVALIDARG` in stress tests.

---
*Document Version: 1.0.0 (Release Stabilization)*
*ModernIPTVPlayer Engineering Team*
