#ifndef BOMAXING_IPC_PROTOCOL_H
#define BOMAXING_IPC_PROTOCOL_H

#include <stdint.h>

#define BMX_IPC_MAGIC 0x314D5842u
#define BMX_IPC_VERSION 1u

typedef enum bmx_ipc_message_type {
    BMX_IPC_PING_REQUEST = 1,
    BMX_IPC_PING_RESPONSE = 2,
    BMX_IPC_API_VERSION_REQUEST = 3,
    BMX_IPC_API_VERSION_RESPONSE = 4,
    BMX_IPC_MANIFEST_REQUEST = 5,
    BMX_IPC_MANIFEST_RESPONSE = 6,
    BMX_IPC_CAPTURE_REQUEST = 7,
    BMX_IPC_CAPTURE_RESPONSE = 8,
    BMX_IPC_ERROR_RESPONSE = 9,
    BMX_IPC_QUIT_REQUEST = 10,
    BMX_IPC_QUIT_RESPONSE = 11,
    BMX_IPC_FEATURES_REQUEST = 12,
    BMX_IPC_FEATURES_RESPONSE = 13,
    BMX_IPC_SET_FEATURE_REQUEST = 14,
    BMX_IPC_SET_FEATURE_RESPONSE = 15
} bmx_ipc_message_type;

/* All integer fields are encoded little-endian on the wire. */
typedef struct bmx_ipc_header {
    uint32_t magic;
    uint16_t version;
    uint16_t message_type;
    uint32_t payload_bytes;
    uint32_t request_id;
} bmx_ipc_header;

#define BMX_IPC_HEADER_BYTES 16u

typedef struct bmx_shared_frame_slot_header {
    uint32_t magic;
    uint32_t version;
    uint32_t payload_bytes;
    uint32_t payload_crc32;
    uint64_t generation;
    uint64_t reserved;
} bmx_shared_frame_slot_header;

#define BMX_SHARED_FRAME_MAGIC 0x31465042u
#define BMX_SHARED_FRAME_VERSION 1u
#define BMX_SHARED_FRAME_HEADER_BYTES 32u

#endif
