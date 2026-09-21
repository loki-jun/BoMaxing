#ifndef BOMAXING_DEVICE_API_H
#define BOMAXING_DEVICE_API_H

#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
#  define BMX_EXPORT __declspec(dllexport)
#  define BMX_CALL __cdecl
#else
#  define BMX_EXPORT __attribute__((visibility("default")))
#  define BMX_CALL
#endif

#define BMX_DEVICE_API_VERSION 1u

typedef struct bmx_frame_desc {
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    uint32_t pixel_format;
    uint64_t sequence;
    int64_t timestamp_unix_ns;
    double unit_scale;
    const void* data;
    size_t data_bytes;
} bmx_frame_desc;

typedef struct bmx_point_cloud_desc {
    uint64_t point_count;
    const float* positions_xyz;
    const float* intensities;
} bmx_point_cloud_desc;

/*
 * Every native camera adapter must export these two functions.
 * The remaining driver operations are intentionally kept behind a
 * versioned adapter implementation or an out-of-process host.
 */
BMX_EXPORT uint32_t BMX_CALL bmx_get_api_version(void);
BMX_EXPORT const char* BMX_CALL bmx_get_manifest_json(void);

#endif
