#include "bomaxing_shared_frame_pool.h"

#include <atomic>
#include <cstring>
#include <limits>

#if defined(_WIN32)
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace {

std::uint32_t read_u32(const std::uint8_t* source) {
    return static_cast<std::uint32_t>(source[0]) |
        (static_cast<std::uint32_t>(source[1]) << 8u) |
        (static_cast<std::uint32_t>(source[2]) << 16u) |
        (static_cast<std::uint32_t>(source[3]) << 24u);
}

std::uint64_t read_u64(const std::uint8_t* source) {
    std::uint64_t value = 0;
    for (unsigned int index = 0; index < 8u; ++index) {
        value |= static_cast<std::uint64_t>(source[index]) << (index * 8u);
    }
    return value;
}

void write_u32(std::uint8_t* destination, std::uint32_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] = static_cast<std::uint8_t>(value >> 8u);
    destination[2] = static_cast<std::uint8_t>(value >> 16u);
    destination[3] = static_cast<std::uint8_t>(value >> 24u);
}

void write_u64(std::uint8_t* destination, std::uint64_t value) {
    for (unsigned int index = 0; index < 8u; ++index) {
        destination[index] = static_cast<std::uint8_t>(value >> (index * 8u));
    }
}

std::uint32_t crc32(const std::uint8_t* payload, std::uint32_t length) {
    std::uint32_t crc = 0xFFFFFFFFu;
    for (std::uint32_t index = 0; index < length; ++index) {
        crc ^= payload[index];
        for (unsigned int bit = 0; bit < 8u; ++bit) {
            crc = (crc & 1u) == 0u
                ? crc >> 1u
                : (crc >> 1u) ^ 0xEDB88320u;
        }
    }
    return ~crc;
}

bool checked_size(
    std::uint32_t slot_count,
    std::uint32_t slot_capacity,
    std::size_t* result) {
    const auto stride = static_cast<std::size_t>(32u) + slot_capacity;
    if (slot_count == 0u ||
        stride < slot_capacity ||
        slot_count > std::numeric_limits<std::size_t>::max() / stride) {
        return false;
    }

    *result = stride * slot_count;
    return true;
}

}  // namespace

namespace bomaxing {

shared_frame_pool::shared_frame_pool(
    const std::string& path,
    std::uint32_t slot_count,
    std::uint32_t slot_capacity)
    : mapping_(nullptr),
      mapping_bytes_(0),
      slot_count_(slot_count),
      slot_capacity_(slot_capacity)
#if defined(_WIN32)
      ,
      file_handle_(INVALID_HANDLE_VALUE),
      mapping_handle_(nullptr)
#else
      ,
      file_descriptor_(-1)
#endif
{
    if (path.empty() || !checked_size(slot_count, slot_capacity, &mapping_bytes_)) {
        return;
    }

#if defined(_WIN32)
    auto file = CreateFileA(
        path.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }

    LARGE_INTEGER size;
    size.QuadPart = static_cast<LONGLONG>(mapping_bytes_);
    if (SetFilePointerEx(file, size, nullptr, FILE_BEGIN) == 0 ||
        SetEndOfFile(file) == 0) {
        CloseHandle(file);
        return;
    }

    auto mapping = CreateFileMappingA(
        file,
        nullptr,
        PAGE_READWRITE,
        size.HighPart,
        size.LowPart,
        nullptr);
    if (mapping == nullptr) {
        CloseHandle(file);
        return;
    }

    auto view = static_cast<std::uint8_t*>(
        MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, mapping_bytes_));
    if (view == nullptr) {
        CloseHandle(mapping);
        CloseHandle(file);
        return;
    }

    file_handle_ = file;
    mapping_handle_ = mapping;
    mapping_ = view;
#else
    file_descriptor_ = open(path.c_str(), O_RDWR | O_CREAT, 0666);
    if (file_descriptor_ < 0 ||
        ftruncate(file_descriptor_, static_cast<off_t>(mapping_bytes_)) != 0) {
        if (file_descriptor_ >= 0) {
            close(file_descriptor_);
            file_descriptor_ = -1;
        }
        return;
    }

    auto view = mmap(
        nullptr,
        mapping_bytes_,
        PROT_READ | PROT_WRITE,
        MAP_SHARED,
        file_descriptor_,
        0);
    if (view == MAP_FAILED) {
        close(file_descriptor_);
        file_descriptor_ = -1;
        return;
    }

    mapping_ = static_cast<std::uint8_t*>(view);
#endif
}

shared_frame_pool::~shared_frame_pool() {
#if defined(_WIN32)
    if (mapping_ != nullptr) {
        UnmapViewOfFile(mapping_);
    }
    if (mapping_handle_ != nullptr) {
        CloseHandle(mapping_handle_);
    }
    if (file_handle_ != INVALID_HANDLE_VALUE) {
        CloseHandle(file_handle_);
    }
#else
    if (mapping_ != nullptr) {
        munmap(mapping_, mapping_bytes_);
    }
    if (file_descriptor_ >= 0) {
        close(file_descriptor_);
    }
#endif
}

bool shared_frame_pool::valid() const noexcept {
    return mapping_ != nullptr;
}

bool shared_frame_pool::publish(
    std::uint32_t slot_index,
    const std::uint8_t* payload,
    std::uint32_t payload_bytes,
    shared_frame_lease* lease) noexcept {
    if (!valid() ||
        lease == nullptr ||
        payload == nullptr ||
        slot_index >= slot_count_ ||
        payload_bytes > slot_capacity_) {
        return false;
    }

    const auto stride = static_cast<std::size_t>(header_bytes) + slot_capacity_;
    auto* slot = mapping_ + stride * slot_index;
    const auto previous_generation = read_u64(slot + 16u);
    const auto generation = previous_generation + 1u;
    std::memcpy(slot + header_bytes, payload, payload_bytes);
    write_u32(slot, magic);
    write_u32(slot + 4u, version);
    write_u32(slot + 8u, payload_bytes);
    write_u32(slot + 12u, crc32(payload, payload_bytes));
    write_u64(slot + 16u, generation);
    write_u64(slot + 24u, 0u);
    std::atomic_thread_fence(std::memory_order_release);
    lease->slot_index = slot_index;
    lease->generation = generation;
    lease->payload_bytes = payload_bytes;
    return true;
}

}  // namespace bomaxing
