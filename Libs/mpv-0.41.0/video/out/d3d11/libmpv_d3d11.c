#include "config.h"
#include <d3d11.h>
#include <d3d11_1.h>
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
    struct mp_hwdec_ctx hwctx;
};

static void destroy_d3d11_pl(struct libmpv_gpu_context *ctx)
{
    struct priv_pl *p = ctx->priv;
    if (!p)
        return;

    OutputDebugStringA("[NATIVE_D3D] destroy_d3d11_pl: START\n");
    MP_INFO(ctx, "[D3D11_PL_DESTROY] Step 1: Destroying RA...\n");

    // Order matters: RA uses pl_gpu, so destroy RA first
    if (p->ra_ctx && p->ra_ctx->ra) {
        struct ra *ra = p->ra_ctx->ra;
        p->ra_ctx->ra = NULL;
        ra->fns->destroy(ra);
        OutputDebugStringA("[NATIVE_D3D] RA destroyed.\n");
        MP_INFO(ctx, "[NATIVE_D3D] RA destroyed.\n");
    }

    if (p->pllog) {
        OutputDebugStringA("[NATIVE_D3D] Destroying pl_log...\n");
        pl_log_destroy(&p->pllog);
        OutputDebugStringA("[NATIVE_D3D] pl_log destroyed.\n");
    }

    // [STABLE SHUTDOWN] Synchronize GPU before releasing Device/Context
    if (p->gpu) {
        OutputDebugStringA("[NATIVE_D3D] Starting pl_gpu_finish (GPU Sync)...\n");
        pl_gpu_finish(p->gpu);
        OutputDebugStringA("[NATIVE_D3D] pl_gpu_finish COMPLETED.\n");
    }

    if (p->d3d11) {
        OutputDebugStringA("[NATIVE_D3D] Destroying pl_d3d11 (libplacebo backend)...\n");
        pl_d3d11_destroy(&p->d3d11);
        OutputDebugStringA("[NATIVE_D3D] pl_d3d11 destroyed.\n");
    }

    OutputDebugStringA("[NATIVE_D3D] Releasing D3D11 COM references...\n");
    if (p->context) {
        ID3D11DeviceContext_Release(p->context);
        p->context = NULL;
    }
    if (p->device) {
        ID3D11Device_Release(p->device);
        p->device = NULL;
    }
    OutputDebugStringA("[NATIVE_D3D] destroy_d3d11_pl: FINISHED\n");

    talloc_free(p);
    ctx->priv = NULL;
}

static int init_d3d11_pl(struct libmpv_gpu_context *ctx, mpv_render_param *params)
{
    ctx->priv = talloc_zero(NULL, struct priv_pl);
    struct priv_pl *p = ctx->priv;

    for (int n = 0; params[n].type != MPV_RENDER_PARAM_INVALID; n++) {
        switch (params[n].type) {
        case 24: // MPV_RENDER_PARAM_D3D11_DEVICE
            p->device = (ID3D11Device *)params[n].data;
            if (p->device)
                ID3D11Device_AddRef(p->device);
            break;
        case 25: // MPV_RENDER_PARAM_D3D11_CONTEXT
            p->context = (ID3D11DeviceContext *)params[n].data;
            if (p->context)
                ID3D11DeviceContext_AddRef(p->context);
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

    
    struct pl_d3d11_params *d3d11_params = talloc_zero(NULL, struct pl_d3d11_params);
    d3d11_params->device = p->device;
    d3d11_params->allow_software = false; // [CRITICAL] Prevent WARP fallback / Green Screen


    p->d3d11 = pl_d3d11_create(p->pllog, d3d11_params);
    talloc_free(d3d11_params);

    if (!p->d3d11) {

        return MPV_ERROR_GENERIC;
    }

    p->gpu = p->d3d11->gpu;

    // 4. Create RA wrapper around pl_gpu

    struct ra *ra = ra_create_pl(p->gpu, ctx->log);
    if (!ra) {

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
    OutputDebugStringA("[NATIVE_D3D] wrap_fbo_pl: START\n");
    struct priv_pl *p = ctx->priv;
    ID3D11Resource *tex = NULL;
    int w = 0, h = 0;
    bool is_shared = false;
    IDXGIKeyedMutex *shared_mutex = NULL;

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


        // [MUTEX] Keys are 0. The host (C#) already acquired the mutex for this thread 
        // before calling mpv_render_context_render. 
        // DO NOT acquire it here, as it causes a DEADLOCK.
        IDXGIKeyedMutex *mutex = NULL;
        if (SUCCEEDED(ID3D11Resource_QueryInterface(tex, &IID_IDXGIKeyedMutex, (void **)&mutex))) {
             // Attach the mutex to the frame destruction context later
             shared_mutex = mutex;
        }
    } else {
        mpv_dxgi_fbo *fbo = get_mpv_render_param(params, MPV_RENDER_PARAM_DXGI_FBO, NULL);
        if (!fbo || !fbo->texture) {

            return MPV_ERROR_INVALID_PARAMETER;
        }

        tex = (ID3D11Resource *)fbo->texture;
        w = fbo->w;
        h = fbo->h;
    }

    // [DIMENSION PROBE] Identity check & dimension discovery
    ID3D11Texture2D *prob_tex = NULL;
    D3D11_TEXTURE2D_DESC desc = {0};
    if (SUCCEEDED(ID3D11Resource_QueryInterface(tex, &IID_ID3D11Texture2D, (void **)&prob_tex))) {
        ID3D11Texture2D_GetDesc(prob_tex, &desc);
        ID3D11Texture2D_Release(prob_tex);
    }



    if (w <= 0) w = desc.Width;
    if (h <= 0) h = desc.Height;

    // [SAFE GUARD] Avoid poison textures (WinUI 1x1 placeholders)
    if (desc.Width < 8 || desc.Height < 8) {
        if (is_shared) ID3D11Resource_Release(tex);
        *out = NULL;
        return 0; // Skip rendering to invalid target
    }

    // [OPTIMIZATION] DiscardView (D3D11.1)
    // Signal the driver that we don't care about previous contents of this view.
    // This reduces memory bandwidth during ResizeBuffers cycles.
    ID3D11DeviceContext1 *ctx1 = NULL;
    if (SUCCEEDED(ID3D11DeviceContext_QueryInterface(p->context, &IID_ID3D11DeviceContext1, (void **)&ctx1))) {
        // [OPTIMIZATION] Discard previous content to signal driver
        // Signal the driver that we don't care about previous contents of this view.
        // This reduces memory bandwidth during ResizeBuffers cycles.
        ID3D11DeviceContext1_DiscardResource(ctx1, tex);
        ID3D11DeviceContext1_Release(ctx1);
    }

    // [FIX] Always wrap at physical texture size.
    // The viewport (render_w/render_h) should NOT shrink the wrap dimensions.
    // libplacebo needs to render to the FULL texture surface.
    // Letterboxing/scaling is handled by mpv's p->dst rect in libmpv_gpu.c.
    int wrap_w = (int)desc.Width;
    int wrap_h = (int)desc.Height;


    pl_tex pl_tex = pl_d3d11_wrap(p->gpu, pl_d3d11_wrap_params(
        .tex = tex,
        .w = wrap_w, .h = wrap_h,
        .fmt = desc.Format,
    ));
    
    if (!pl_tex) {
         MP_ERR(ctx, "Failed to wrap D3D11 texture into pl_tex (%dx%d)!\n", wrap_w, wrap_h);
         return MPV_ERROR_UNSUPPORTED;
    }

    // [FIX] Manually override capability flags
    // Libplacebo sometimes misdetects capabilities on WinUI 3 shared textures.
    struct pl_tex_params *mut_params = (struct pl_tex_params *)&pl_tex->params;
    mut_params->sampleable = true;
    mut_params->renderable = true;
    mut_params->blit_dst = true;
    mut_params->blit_src = true;

    // 2. Wrap pl_tex into ra_tex for MPV
    // mppl_wrap_tex is the bridge helper from ra_pl.h
    *out = talloc_zero(NULL, struct ra_tex);
    if (!mppl_wrap_tex(p->ra_ctx->ra, pl_tex, *out)) {
         MP_ERR(ctx, "Failed to wrap pl_tex into ra_tex!\n");
         pl_tex_destroy(p->gpu, &pl_tex);
         if (is_shared) ID3D11Resource_Release(tex);
         if (shared_mutex) {
             IDXGIKeyedMutex_Release(shared_mutex);
         }
         talloc_free(*out);
         *out = NULL;
         return MPV_ERROR_UNSUPPORTED;
    }

    // [ZERO-COPY CLEANUP] Attach destructor to ra_tex to release the shared resource
    if (is_shared) {
        ID3D11Resource **owner = talloc_size(*out, sizeof(ID3D11Resource *));
        if (owner) {
            *owner = tex;
            talloc_set_destructor(owner, release_shared_tex);
        }
        
        if (shared_mutex) {
            IDXGIKeyedMutex **m_owner = talloc_size(*out, sizeof(IDXGIKeyedMutex *));
            if (m_owner) {
                *m_owner = shared_mutex;
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
