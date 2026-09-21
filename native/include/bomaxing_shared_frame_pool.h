#ifndef BOMAXING_SHARED_FRAME_POOL_H
#define BOMAXING_SHARED_FRAME_POOL_H

#include <cstddef>
#include <cstdint>
#include <string>

namespace bomaxing {

struct shared_frame_lease {
    std::uint32_t slot_index;
    std::uint64_t generation;
    std::uint32_t payload_bytes;
};

class shared_frame_pool {
public:
    shared_frame_pool(
        const std::string& path,
        std::uint32_t slot_count,
        std::uint32_t slot_capacity);
    ~shared_frame_pool();

    shared_frame_pool(const shared_frame_pool&) = delete;
    shared_frame_pool& operator=(const shared_frame_pool&) = delete;

    bool valid() const noexcept;
    bool publish(
        std::uint32_t slot_index,
        const std::uint8_t* payload,
        std::uint32_t payload_bytes,
        shared_frame_lease* lease) noexcept;

private:
    static constexpr std::uint32_t header_bytes = 32u;
    static constexpr std::uint32_t magic = 0x31465042u;
    static constexpr std::uint32_t version = 1u;

    std::uint8_t* mapping_;
    std::size_t mapping_bytes_;
    std::uint32_t slot_count_;
    std::uint32_t slot_capacity_;
#if defined(_WIN32)
    void* file_handle_;
    void* mapping_handle_;
#else
    int file_descriptor_;
#endif
};

}  // namespace bomaxing

#endif
