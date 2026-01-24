/* Copyright (C) 2024 the mpv developers
 *
 * Permission to use, copy, modify, and/or distribute this software for any
 * purpose with or without fee is hereby granted, provided that the above
 * copyright notice and this permission notice appear in all copies.
 *
 * THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 * WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 * ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 * WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 * ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 * OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */

#ifndef MPV_CLIENT_API_RENDER_DXGI_H_
#define MPV_CLIENT_API_RENDER_DXGI_H_

#include "render.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Predefined value for MPV_RENDER_PARAM_API_TYPE.
 */
#define MPV_RENDER_API_TYPE_DXGI "dxgi"

/**
 * Parameters for mpv_render_param_type.
 */
#define MPV_RENDER_PARAM_DXGI_INIT_PARAMS 21
#define MPV_RENDER_PARAM_DXGI_FBO 22

typedef struct mpv_dxgi_init_params {
    /**
     * ID3D11Device*
     */
    void *device;
    /**
     * ID3D11DeviceContext*
     */
    void *context;
} mpv_dxgi_init_params;

typedef struct mpv_dxgi_fbo {
    /**
     * ID3D11Texture2D*
     */
    void *texture;
    /**
     * Internal texture width/height.
     */
    int w, h;
} mpv_dxgi_fbo;

#ifdef __cplusplus
}
#endif

#endif
