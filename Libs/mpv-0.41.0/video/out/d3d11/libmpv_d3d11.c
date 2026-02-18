#include "config.h"
#include <d3d11.h>
#include "mpv/render_dxgi.h"
#include "video/out/gpu/libmpv_gpu.h"
#include "video/out/gpu/ra.h"
#include "video/out/gpu/spirv.h"
#include "ra_d3d11.h"

// --- GPU-NEXT INCLUDES ---
#include <libplacebo/d3d11.h>
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
    struct ra_ctx *ra_ctx;
    pl_log pllog;
    pl_d3d11 d3d11;
    pl_gpu gpu;
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
    if (p->d3d11)
        pl_d3d11_destroy(&p->d3d11);

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
    // 1. Get D3D11 Device from params using the standard int value 24 (MPV_RENDER_PARAM_D3D11_DEVICE)
    // We cannot use the enum constant explicitly if it's not defined in the headers we have, 
    // but we know it's 24 from render.h
    void *device = get_mpv_render_param(params, 24, NULL); 
    if (!device) {
        MP_ERR(ctx, "MPV_RENDER_PARAM_D3D11_DEVICE (24) not found or null!\n");
        return MPV_ERROR_INVALID_PARAMETER;
    }

    ctx->priv = talloc_zero(NULL, struct priv_pl);
    struct priv_pl *p = ctx->priv;

    // 2. Create pl_log bridge (libplacebo requires pl_log, not mp_log)
    p->pllog = mppl_log_create(p, ctx->log);
    if (!p->pllog) {
        MP_ERR(ctx, "Failed to create pl_log for gpu-next backend!\n");
        talloc_free(p);
        return MPV_ERROR_GENERIC;
    }

    // 3. Create libplacebo D3D11 context
    // NOTE: device is void* (MPV_RENDER_PARAM_D3D11_DEVICE data), which points to ID3D11Device*
    ID3D11Device *d3d11_device = *(ID3D11Device **)device;

    p->d3d11 = pl_d3d11_create(p->pllog, &(struct pl_d3d11_params) {
        .device = d3d11_device,
    });
    
    if (!p->d3d11) {
        MP_ERR(ctx, "Failed to create pl_d3d11 context!\n");
        return MPV_ERROR_UNSUPPORTED;
    }

    p->gpu = p->d3d11->gpu;

    // 3. Create RA wrapper around pl_gpu
    // This allows MPV to talk to libplacebo via the 'ra' interface
    struct ra *ra = ra_create_pl(p->gpu, ctx->log);
    if (!ra) {
        MP_ERR(ctx, "Failed to create ra_pl wrapper!\n");
        destroy_d3d11_pl(ctx);
        return MPV_ERROR_UNSUPPORTED;
    }

    // 4. Setup internal context
    p->ra_ctx = talloc_zero(p, struct ra_ctx);
    p->ra_ctx->log = ctx->log;
    p->ra_ctx->global = ctx->global;
    p->ra_ctx->ra = ra;
    ctx->ra_ctx = p->ra_ctx;

    MP_INFO(ctx, "Successfully initialized d3d11 (gpu-next) backend!\n");
    return 0;
}

static int wrap_fbo_pl(struct libmpv_gpu_context *ctx, mpv_render_param *params,
                       struct ra_tex **out)
{
    struct priv_pl *p = ctx->priv;
    
    // We reuse the existing DXGI FBO param structure since it contains the texture ptr
    mpv_dxgi_fbo *fbo = get_mpv_render_param(params, MPV_RENDER_PARAM_DXGI_FBO, NULL);
    if (!fbo || !fbo->texture)
        return MPV_ERROR_INVALID_PARAMETER;

    // 1. Wrap D3D11 Texture into pl_tex (Zero-Copy)
    // This just creates a view, no data copying happens here.
    pl_tex pl_tex = pl_d3d11_wrap(p->gpu, pl_d3d11_wrap_params(
        .tex = (ID3D11Texture2D *)fbo->texture,
        .w = 0, .h = 0, // 0 means auto-detect from texture resource
    ));
    
    if (!pl_tex) {
         MP_ERR(ctx, "Failed to wrap D3D11 texture into pl_tex!\n");
         return MPV_ERROR_UNSUPPORTED;
    }

    // 2. Wrap pl_tex into ra_tex for MPV
    // mppl_wrap_tex is the bridge helper we found in ra_pl.h
    *out = talloc_zero(NULL, struct ra_tex);
    if (!mppl_wrap_tex(p->ra_ctx->ra, pl_tex, *out)) {
         MP_ERR(ctx, "Failed to wrap pl_tex into ra_tex!\n");
         pl_tex_destroy(p->gpu, &pl_tex);
         talloc_free(*out);
         return MPV_ERROR_UNSUPPORTED;
    }

    // Force render_dst and blit_dst flags on the wrapped texture.
    // The SwapChainPanel backbuffer has D3D11_BIND_RENDER_TARGET, but
    // pl_d3d11_wrap doesn't always set blit_dst. The legacy ra_d3d11
    // backend sets these from bind_flags (see ra_d3d11.c:599-600).
    (*out)->params.render_dst = true;
    (*out)->params.blit_dst = true;
    
    return 0;
}

static void done_frame_pl(struct libmpv_gpu_context *ctx, bool ds)
{
    struct priv_pl *p = ctx->priv;
    // OPTIMIZATION: Use flush() instead of finish()
    // This submits the command buffer to the GPU queue and returns immediately.
    // We rely on D3D11/DXGI swapchain sync for the actual display timing.
    pl_gpu_flush(p->gpu);
}

const struct libmpv_gpu_context_fns libmpv_gpu_context_d3d11_next = {
    .api_name = "d3d11", // New API string for gpu-next
    .init = init_d3d11_pl,
    .wrap_fbo = wrap_fbo_pl,
    .done_frame = done_frame_pl,
    .destroy = destroy_d3d11_pl,
};
