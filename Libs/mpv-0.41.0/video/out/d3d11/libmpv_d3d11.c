#include "config.h"
#include <d3d11.h>
#include "mpv/render_dxgi.h"
#include "video/out/gpu/libmpv_gpu.h"
#include "video/out/gpu/ra.h"
#include "video/out/gpu/spirv.h"
#include "ra_d3d11.h"

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
