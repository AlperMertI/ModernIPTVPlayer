#pragma once

#include "video/out/gpu/ra.h"
#include <libplacebo/gpu.h>

#include <d3d11.h>

struct ra *ra_create_pl(pl_gpu gpu, struct mp_log *log);

pl_gpu ra_pl_get(const struct ra *ra);

static inline pl_fmt ra_pl_fmt_get(const struct ra_format *format)
{
    return format->priv;
}

// Wrap a pl_tex into a ra_tex struct, returns if successful
bool mppl_wrap_tex(struct ra *ra, pl_tex pltex, struct ra_tex *out_tex);

// Modern path: Wrap a D3D11 video texture directly using Libplacebo
struct ra_tex *ra_pl_wrap_d3d11_video(struct ra *ra, ID3D11Texture2D *res,
                                       int w, int h, int array_slice,
                                       DXGI_FORMAT dxgi_fmt,
                                       const struct ra_format *ra_fmt);


