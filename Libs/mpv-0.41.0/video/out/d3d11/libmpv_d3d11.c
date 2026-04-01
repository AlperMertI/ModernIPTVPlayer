#include "config.h"
#include <d3d11.h>
#include <windows.h>
#include <stdio.h>
#include "mpv/render_dxgi.h"
#include "video/out/gpu/libmpv_gpu.h"
#include "video/out/gpu/ra.h"
extern void gpu_sync_trace(const char *msg);
#include "video/out/gpu/spirv.h"
#include "ra_d3d11.h"

// --- GPU-NEXT INCLUDES ---
#include <libplacebo/d3d11.h>
#include "video/hwdec.h"
#include "video/d3d.h"

// --- GN LOG (same as libmpv_gpu.c) ---
static FILE *wrap_log_fp = NULL;
static void wrap_log_init(void) {
    if (!wrap_log_fp) {
        wrap_log_fp = fopen("C:\\Users\\ASUS\\Documents\\ModernIPTVPlayer\\gn_debug.log", "a");
        if (wrap_log_fp) {
            fprintf(wrap_log_fp, "=== WRAP_FBO LOG SESSION START ===\n");
            fflush(wrap_log_fp);
        }
    }
}
#define WRAP_LOG(fmt, ...) do { \
    wrap_log_init(); \
    char _wbuf[512]; \
    snprintf(_wbuf, sizeof(_wbuf), "[WRAP_FBO] " fmt "\n", ##__VA_ARGS__); \
    if (wrap_log_fp) { fprintf(wrap_log_fp, "%s", _wbuf); fflush(wrap_log_fp); } \
    OutputDebugStringA(_wbuf); \
} while(0)
#include "video/out/placebo/ra_pl.h"
#include "video/out/placebo/utils.h"
// -------------------------

struct priv {
    struct ra_ctx *ra_ctx;
    struct spirv_compiler *spirv;
};

static int init(struct libmpv_gpu_context *ctx, mpv_render_param *params)
{
    ctx->priv = talloc_zero(NULL, struct priv);
    struct priv *p = ctx->priv;

    mpv_dxgi_init_params *init_params =
        get_mpv_render_param(params, MPV_RENDER_PARAM_DXGI_INIT_PARAMS, NULL);
    if (!init_params || !init_params->device)
        return MPV_ERROR_INVALID_PARAMETER;

    p->ra_ctx = talloc_zero(p, struct ra_ctx);
    p->ra_ctx->log = ctx->log;
    p->ra_ctx->global = ctx->global;

    // Initialize SPIRV compiler (required for D3D11 backend)
    if (!spirv_compiler_init(p->ra_ctx)) {
        MP_ERR(ctx, "Failed to initialize SPIRV compiler for DXGI backend\n");
        return MPV_ERROR_UNSUPPORTED;
    }
    p->spirv = p->ra_ctx->spirv;

    p->ra_ctx->ra = ra_d3d11_create(init_params->device, ctx->log, p->spirv);
    if (!p->ra_ctx->ra)
        return MPV_ERROR_UNSUPPORTED;

    ctx->ra_ctx = p->ra_ctx;

    return 0;
}


static int wrap_fbo(struct libmpv_gpu_context *ctx, mpv_render_param *params,
                    struct ra_tex **out)
{
    struct priv *p = ctx->priv;

    mpv_dxgi_fbo *fbo =
        get_mpv_render_param(params, MPV_RENDER_PARAM_DXGI_FBO, NULL);
    if (!fbo || !fbo->texture)
        return MPV_ERROR_INVALID_PARAMETER;

    *out = ra_d3d11_wrap_tex(p->ra_ctx->ra, (ID3D11Resource *)fbo->texture);
    if (!*out)
        return MPV_ERROR_UNSUPPORTED;

    return 0;
}

static void done_frame(struct libmpv_gpu_context *ctx, bool ds)
{
    struct priv *p = ctx->priv;
    ra_d3d11_flush(p->ra_ctx->ra);
}

static void destroy(struct libmpv_gpu_context *ctx)
{
    struct priv *p = ctx->priv;

    if (p->ra_ctx && p->ra_ctx->ra)
        ra_free(&p->ra_ctx->ra);
}

const struct libmpv_gpu_context_fns libmpv_gpu_context_d3d11 = {
    .api_name = MPV_RENDER_API_TYPE_DXGI,
    .init = init,
    .wrap_fbo = wrap_fbo,
    .done_frame = done_frame,
    .destroy = destroy,
};

// ============================================================================
// GPU-NEXT BACKEND (Dual Backend Strategy)
// ============================================================================

struct priv_pl {
    ID3D11Device *device;
    ID3D11DeviceContext *context;
    pl_log pllog;
    pl_d3d11 d3d11;
    pl_gpu gpu;
    struct ra_ctx *ra_ctx;
    IDXGIKeyedMutex *shared_mutex;
    struct mp_hwdec_ctx hwctx;
};

static void destroy_d3d11_pl(struct libmpv_gpu_context *ctx)
{
    struct priv_pl *p = ctx->priv;
    if (!p)
        return;

    MP_INFO(ctx, "[D3D11_PL_DESTROY] Step 1: Destroying RA...\n");

    // Order matters: RA uses pl_gpu, so destroy RA first
    if (p->ra_ctx && p->ra_ctx->ra) {
        struct ra *ra = p->ra_ctx->ra;
        p->ra_ctx->ra = NULL;
        ra->fns->destroy(ra);
    }

    MP_INFO(ctx, "[D3D11_PL_DESTROY] Step 2: RA destroyed. Destroying pl_d3d11...\n");

    // Then destroy pl_d3d11 (which owns pl_gpu)
    if (p->d3d11) {
        hwdec_devices_remove(ctx->hwdec_devs, &p->hwctx);
        av_buffer_unref(&p->hwctx.av_device_ref);
        pl_d3d11_destroy(&p->d3d11);
    }

    MP_INFO(ctx, "[D3D11_PL_DESTROY] Step 3: pl_d3d11 destroyed. Destroying pl_log...\n");

    // Finally destroy pl_log
    if (p->pllog)
        pl_log_destroy(&p->pllog);

    MP_INFO(ctx, "[D3D11_PL_DESTROY] Step 4: pl_log destroyed. talloc_free(p)...\n");

    talloc_free(p);
    ctx->priv = NULL;

    // Note: can't log after this point since p (and its log context) is freed
}

static int init_d3d11_pl(struct libmpv_gpu_context *ctx, mpv_render_param *params)
{
    ctx->priv = talloc_zero(NULL, struct priv_pl);
    struct priv_pl *p = ctx->priv;

    for (int n = 0; params[n].type != MPV_RENDER_PARAM_INVALID; n++) {
        switch (params[n].type) {
        case 24: // MPV_RENDER_PARAM_D3D11_DEVICE
            p->device = (ID3D11Device *)params[n].data;
            break;
        case 25: // MPV_RENDER_PARAM_D3D11_CONTEXT
            p->context = (ID3D11DeviceContext *)params[n].data;
            break;
        }
    }

    if (!p->device) {
        gpu_sync_trace("INIT: FAIL - No D3D11 Device provided");
        return MPV_ERROR_INVALID_PARAMETER;
    }
    
    if (!p->context) {
        ID3D11Device_GetImmediateContext(p->device, &p->context);
        gpu_sync_trace("INIT: WARNING - Context not provided, using GetImmediateContext");
    }

    // 2. Create pl_log bridge
    gpu_sync_trace("INIT: Creating pl_log...");
    p->pllog = mppl_log_create(p, ctx->log);
    if (!p->pllog) {
        gpu_sync_trace("INIT: FAIL - pl_log creation");
        talloc_free(p);
        return MPV_ERROR_GENERIC;
    }

    // 3. Create libplacebo D3D11 context
    gpu_sync_trace("INIT: Calling pl_d3d11_create (Hard Hardware Lock)...");

    const struct pl_d3d11_params d3d11_params = {
        .device = p->device,
        .allow_software = false, // [CRITICAL] Prevent WARP fallback / Green Screen
    };
    p->d3d11 = pl_d3d11_create(p->pllog, &d3d11_params);

    if (!p->d3d11) {
        gpu_sync_trace("INIT: FAIL - pl_d3d11_create (Feature level mismatch)");
        return MPV_ERROR_GENERIC;
    }

    gpu_sync_trace("INIT: SUCCESS - Hardware GPU is active");
    p->gpu = p->d3d11->gpu;

    // 4. Create RA wrapper around pl_gpu
    gpu_sync_trace("INIT: Calling ra_create_pl...");
    struct ra *ra = ra_create_pl(p->gpu, ctx->log);
    if (!ra) {
        gpu_sync_trace("INIT: FAIL - ra_create_pl");
        destroy_d3d11_pl(ctx);
        return MPV_ERROR_UNSUPPORTED;
    }
    
    // [HWDEC INTEROP] Add native resource so d3d11va driver can find the device
    ra_add_native_resource(ra, "d3d11_device_ptr", p->device);
    
    // 5. Setup internal context
    p->ra_ctx = talloc_zero(p, struct ra_ctx);
    p->ra_ctx->log = ctx->log;
    p->ra_ctx->global = ctx->global;
    p->ra_ctx->ra = ra;
    ctx->ra_ctx = p->ra_ctx; // [CRITICAL] Link for HWDEC engine to see it

    // 6. Register the D3D11 device for HWDEC (D3D11VA Zero-Copy)
    gpu_sync_trace("INIT: Registering D3D11 device for HWDEC...");
    static const int subfmts[] = {IMGFMT_NV12, IMGFMT_P010, 0};
    p->hwctx = (struct mp_hwdec_ctx){
        .driver_name = "d3d11va",
        .av_device_ref = d3d11_wrap_device_ref(p->device),
        .supported_formats = subfmts,
        .hw_imgfmt = IMGFMT_D3D11,
    };

    if (p->hwctx.av_device_ref) {
        hwdec_devices_add(ctx->hwdec_devs, &p->hwctx);
        gpu_sync_trace("INIT: HWDEC Device Registered Successfully.");
    } else {
        gpu_sync_trace("INIT: ERROR - Failed to wrap D3D11 device for HWDEC.");
    }

    gpu_sync_trace("INIT: Completed init_d3d11_pl Successfully. RA Context is LIVE.");
    return 0;
}

static void release_shared_tex(void *ptr)
{
    ID3D11Resource *tex = *(ID3D11Resource **)ptr;
    if (tex)
        ID3D11Resource_Release(tex);
}

static void release_shared_mutex(void *ptr)
{
    IDXGIKeyedMutex *mutex = *(IDXGIKeyedMutex **)ptr;
    if (mutex)
        IDXGIKeyedMutex_Release(mutex);
}

static int wrap_fbo_pl(struct libmpv_gpu_context *ctx, mpv_render_param *params,
                       struct ra_tex **out)
{
    struct priv_pl *p = ctx->priv;
    ID3D11Resource *tex = NULL;
    int w = 0, h = 0;
    bool is_shared = false;

    // [ZERO-COPY] Check for shared texture handle first
    void *shared_handle = get_mpv_render_param(params, MPV_RENDER_PARAM_DXGI_SHARED_TEXTURE, NULL);
    if (shared_handle) {
        ID3D11Texture2D *shared_tex = NULL;
        HRESULT hr = ID3D11Device_OpenSharedResource(p->device, (HANDLE)shared_handle, 
                                                      &IID_ID3D11Texture2D, (void **)&shared_tex);
        if (FAILED(hr)) {
            gpu_sync_trace("SHARED_OPEN_FAIL: Failed to open shared resource handle");
            return MPV_ERROR_GENERIC;
        }
        tex = (ID3D11Resource *)shared_tex;
        is_shared = true;

        // [MUTEX] Try to get keyed mutex for synchronization
        if (SUCCEEDED(ID3D11Resource_QueryInterface(tex, &IID_IDXGIKeyedMutex, (void **)&p->shared_mutex))) {
            // We'll release it in done_frame_pl or if wrap fails
            // Actually, we should only acquire it once per frame.
            // For now, let's just acquire it here.
            HRESULT hr_m = IDXGIKeyedMutex_AcquireSync(p->shared_mutex, 0, 100);
            if (FAILED(hr_m)) {
                gpu_sync_trace("MUTEX_FAIL: Failed to acquire shared mutex");
                // Continue anyway? Or fail? Let's continue for now but log.
            }
        }
    } else {
        mpv_dxgi_fbo *fbo = get_mpv_render_param(params, MPV_RENDER_PARAM_DXGI_FBO, NULL);
        if (!fbo || !fbo->texture)
            return MPV_ERROR_INVALID_PARAMETER;
        tex = (ID3D11Resource *)fbo->texture;
        w = fbo->w;
        h = fbo->h;
        WRAP_LOG("C# sent fbo->texture=%p fbo->w=%d fbo->h=%d", (void*)fbo->texture, fbo->w, fbo->h);
    }

    // [DIMENSION PROBE] Identity check & dimension discovery
    ID3D11Texture2D *prob_tex = NULL;
    D3D11_TEXTURE2D_DESC desc = {0};
    if (SUCCEEDED(ID3D11Resource_QueryInterface(tex, &IID_ID3D11Texture2D, (void **)&prob_tex))) {
        ID3D11Texture2D_GetDesc(prob_tex, &desc);
        ID3D11Texture2D_Release(prob_tex);
    }

    WRAP_LOG("Texture desc=%ux%u | using w=%d h=%d", desc.Width, desc.Height, w, h);

    if (w <= 0) w = desc.Width;
    if (h <= 0) h = desc.Height;

    // [SAFE GUARD] Avoid poison textures (WinUI 1x1 placeholders)
    if (desc.Width < 8 || desc.Height < 8) {
        if (is_shared) ID3D11Resource_Release(tex);
        *out = NULL;
        return 0; // Skip rendering to invalid target
    }

    pl_tex pl_tex = pl_d3d11_wrap(p->gpu, pl_d3d11_wrap_params(
        .tex = tex,
        .w = w, .h = h,
        .fmt = desc.Format,
    ));
    
    if (!pl_tex) {
         MP_ERR(ctx, "Failed to wrap D3D11 texture into pl_tex (%dx%d)!\n", w, h);
         return MPV_ERROR_UNSUPPORTED;
    }

    // [ROOT CAUSE FIX] Force blit_dst/blit_src on the underlying pl_tex.
    //
    // WHY: libplacebo's pl_tex_clear_ex() (gpu.c:318) requires dst->params.blit_dst.
    //      WinUI textures often miss these flags during automatic wrapping.
    struct pl_tex_params *mutable_params = (struct pl_tex_params *)&pl_tex->params;
    mutable_params->blit_dst = true;
    mutable_params->blit_src = true;
    mutable_params->renderable = true; // Ensure it can be cleared

    // 2. Wrap pl_tex into ra_tex for MPV
    // mppl_wrap_tex is the bridge helper from ra_pl.h
    *out = talloc_zero(NULL, struct ra_tex);
    if (!mppl_wrap_tex(p->ra_ctx->ra, pl_tex, *out)) {
         gpu_sync_trace("WRAP_FAIL: mppl_wrap_tex failed");
         MP_ERR(ctx, "Failed to wrap pl_tex into ra_tex!\n");
         pl_tex_destroy(p->gpu, &pl_tex);
         if (is_shared) ID3D11Resource_Release(tex);
         if (p->shared_mutex) {
             IDXGIKeyedMutex_ReleaseSync(p->shared_mutex, 0);
             IDXGIKeyedMutex_Release(p->shared_mutex);
             p->shared_mutex = NULL;
         }
         talloc_free(*out);
         *out = NULL; // Safety: Never return partially allocated junk
         return MPV_ERROR_UNSUPPORTED;
    }

    // [ZERO-COPY CLEANUP] Attach destructor to ra_tex to release the shared resource
    if (is_shared) {
        ID3D11Resource **owner = talloc_size(*out, sizeof(ID3D11Resource *));
        if (owner) {
            *owner = tex;
            talloc_set_destructor(owner, release_shared_tex);
        }
        
        if (p->shared_mutex) {
            IDXGIKeyedMutex **m_owner = talloc_size(*out, sizeof(IDXGIKeyedMutex *));
            if (m_owner) {
                *m_owner = p->shared_mutex;
                talloc_set_destructor(m_owner, release_shared_mutex);
            }
        }
    }

    // ra_tex params are populated by mppl_wrap_tex from pl_tex->params,
    // so blit_dst/blit_src will now be true at both layers.
    
    return 0;
}


static void done_frame_pl(struct libmpv_gpu_context *ctx, bool ds)
{
    struct priv_pl *p = ctx->priv;
    
    // Release shared mutex if held
    if (p->shared_mutex) {
        IDXGIKeyedMutex_ReleaseSync(p->shared_mutex, 0);
        // We don't release the interface here, talloc destructor does it
        p->shared_mutex = NULL; 
    }

    // OPTIMIZATION: Use flush() instead of finish()
    pl_gpu_flush(p->gpu);
}

const struct libmpv_gpu_context_fns libmpv_gpu_context_d3d11_next = {
    .api_name = "d3d11", // New API string for gpu-next
    .init = init_d3d11_pl,
    .wrap_fbo = wrap_fbo_pl,
    .done_frame = done_frame_pl,
    .destroy = destroy_d3d11_pl,
};
