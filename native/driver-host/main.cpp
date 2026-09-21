#include "bomaxing_device_api.h"
#include "bomaxing_ipc_protocol.h"
#include "bomaxing_shared_frame_pool.h"

#include <array>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <memory>
#include <string>
#include <vector>

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#endif

namespace {

constexpr std::uint32_t kMaxIpcPayload = 64u * 1024u * 1024u;
constexpr const char* kManifest =
    R"({"pluginId":"demo.native-camera","displayName":"BoMaxing Native Camera Demo",)"
    R"("version":"0.1.0","deviceKind":"Camera3D","transport":"OutOfProcess","abiVersion":"1"})";
constexpr const char* kFeatures =
    R"([{"name":"ExposureTime","value":"1000","isReadOnly":false},)"
    R"({"name":"Gain","value":"1.0","isReadOnly":false}])";
constexpr std::array<std::uint8_t, 12> kDemoFrame = {
    0, 0, 255, 0,
    0, 0, 255, 255,
    0, 0, 0, 0};

struct options {
    bool binary_ipc = false;
    std::string frame_pool_path;
};

std::uint16_t read_u16(const std::uint8_t* source) {
    return static_cast<std::uint16_t>(source[0]) |
        (static_cast<std::uint16_t>(source[1]) << 8u);
}

std::uint32_t read_u32(const std::uint8_t* source) {
    return static_cast<std::uint32_t>(source[0]) |
        (static_cast<std::uint32_t>(source[1]) << 8u) |
        (static_cast<std::uint32_t>(source[2]) << 16u) |
        (static_cast<std::uint32_t>(source[3]) << 24u);
}

void write_u16(std::uint8_t* destination, std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] = static_cast<std::uint8_t>(value >> 8u);
}

void write_u32(std::uint8_t* destination, std::uint32_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] = static_cast<std::uint8_t>(value >> 8u);
    destination[2] = static_cast<std::uint8_t>(value >> 16u);
    destination[3] = static_cast<std::uint8_t>(value >> 24u);
}

bool read_exact(
    std::istream& input,
    std::uint8_t* destination,
    std::size_t bytes) {
    input.read(
        reinterpret_cast<char*>(destination),
        static_cast<std::streamsize>(bytes));
    return input.good();
}

bool write_all(
    std::ostream& output,
    const std::uint8_t* source,
    std::size_t bytes) {
    output.write(
        reinterpret_cast<const char*>(source),
        static_cast<std::streamsize>(bytes));
    output.flush();
    return output.good();
}

std::string make_capture_json(
    std::uint64_t sequence,
    const bomaxing::shared_frame_lease* lease) {
    const auto timestamp = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    std::string json =
        R"({"deviceId":"demo-camera","sequence":)" +
        std::to_string(sequence) +
        R"(,"timestampUnixNs":)" +
        std::to_string(timestamp) +
        R"(,"width":4,"height":3,"stride":4,"pixelFormat":"Gray8",)"
        R"("unitScale":1.0,"coordinateSystem":"Camera",)"
        R"("dataHex":"0000ff000000ffff00000000")";
    if (lease != nullptr) {
        json +=
            R"(,"sharedFrameSlot":)" +
            std::to_string(lease->slot_index) +
            R"(,"sharedFrameGeneration":)" +
            std::to_string(lease->generation);
    }
    json += "}";
    return json;
}

void print_capture(
    std::ostream& output,
    bomaxing::shared_frame_pool* frame_pool,
    std::uint64_t sequence) {
    bomaxing::shared_frame_lease lease{};
    const auto published = frame_pool != nullptr &&
        frame_pool->publish(
            0,
            kDemoFrame.data(),
            static_cast<std::uint32_t>(kDemoFrame.size()),
            &lease);
    output << make_capture_json(sequence, published ? &lease : nullptr) << '\n';
}

void run_line_protocol(bomaxing::shared_frame_pool* frame_pool) {
    std::string line;
    std::uint64_t sequence = 0;
    while (std::getline(std::cin, line)) {
        if (line == "manifest") {
            std::cout << kManifest << std::endl;
        } else if (line == "api_version") {
            std::cout << BMX_DEVICE_API_VERSION << std::endl;
        } else if (line == "ping") {
            std::cout << "pong" << std::endl;
        } else if (line == "capture") {
            print_capture(std::cout, frame_pool, ++sequence);
        } else if (line == "features") {
            std::cout << kFeatures << std::endl;
        } else if (line.rfind("set_feature|", 0) == 0) {
            std::cout << "ok" << std::endl;
        } else if (line == "quit") {
            break;
        } else {
            std::cout << R"({"error":"unknown_command"})" << std::endl;
        }
    }
}

std::vector<std::uint8_t> text_payload(const std::string& value) {
    return std::vector<std::uint8_t>(value.begin(), value.end());
}

bool send_binary_response(
    std::uint16_t message_type,
    std::uint32_t request_id,
    const std::vector<std::uint8_t>& payload) {
    if (payload.size() > kMaxIpcPayload) {
        return false;
    }

    std::array<std::uint8_t, BMX_IPC_HEADER_BYTES> header{};
    write_u32(header.data(), BMX_IPC_MAGIC);
    write_u16(header.data() + 4u, BMX_IPC_VERSION);
    write_u16(header.data() + 6u, message_type);
    write_u32(header.data() + 8u, static_cast<std::uint32_t>(payload.size()));
    write_u32(header.data() + 12u, request_id);
    return write_all(std::cout, header.data(), header.size()) &&
        (payload.empty() || write_all(std::cout, payload.data(), payload.size()));
}

bool run_binary_protocol(bomaxing::shared_frame_pool* frame_pool) {
    std::array<std::uint8_t, BMX_IPC_HEADER_BYTES> header{};
    std::uint64_t sequence = 0;
    while (read_exact(std::cin, header.data(), header.size())) {
        const auto magic = read_u32(header.data());
        const auto version = read_u16(header.data() + 4u);
        const auto message_type = read_u16(header.data() + 6u);
        const auto payload_bytes = read_u32(header.data() + 8u);
        const auto request_id = read_u32(header.data() + 12u);
        if (magic != BMX_IPC_MAGIC ||
            version != BMX_IPC_VERSION ||
            payload_bytes > kMaxIpcPayload) {
            return false;
        }

        std::vector<std::uint8_t> request(payload_bytes);
        if (!request.empty() &&
            !read_exact(std::cin, request.data(), request.size())) {
            return false;
        }

        std::uint16_t response_type = BMX_IPC_ERROR_RESPONSE;
        std::vector<std::uint8_t> response;
        bool should_quit = false;
        switch (message_type) {
        case BMX_IPC_PING_REQUEST:
            response_type = BMX_IPC_PING_RESPONSE;
            response = text_payload("pong");
            break;
        case BMX_IPC_API_VERSION_REQUEST:
            response_type = BMX_IPC_API_VERSION_RESPONSE;
            response.resize(sizeof(std::uint32_t));
            write_u32(response.data(), BMX_DEVICE_API_VERSION);
            break;
        case BMX_IPC_MANIFEST_REQUEST:
            response_type = BMX_IPC_MANIFEST_RESPONSE;
            response = text_payload(kManifest);
            break;
        case BMX_IPC_CAPTURE_REQUEST: {
            response_type = BMX_IPC_CAPTURE_RESPONSE;
            bomaxing::shared_frame_lease lease{};
            const auto published = frame_pool != nullptr &&
                frame_pool->publish(
                    0,
                    kDemoFrame.data(),
                    static_cast<std::uint32_t>(kDemoFrame.size()),
                    &lease);
            const auto json = make_capture_json(
                ++sequence,
                published ? &lease : nullptr);
            response = text_payload(json);
            break;
        }
        case BMX_IPC_FEATURES_REQUEST:
            response_type = BMX_IPC_FEATURES_RESPONSE;
            response = text_payload(kFeatures);
            break;
        case BMX_IPC_SET_FEATURE_REQUEST:
            response_type = BMX_IPC_SET_FEATURE_RESPONSE;
            response = text_payload("ok");
            break;
        case BMX_IPC_QUIT_REQUEST:
            response_type = BMX_IPC_QUIT_RESPONSE;
            should_quit = true;
            break;
        default:
            response = text_payload(R"({"error":"unknown_command"})");
            break;
        }

        if (!send_binary_response(response_type, request_id, response)) {
            return false;
        }
        if (should_quit) {
            return true;
        }
    }

    return true;
}

bool parse_options(int argc, char** argv, options* result) {
    for (int index = 1; index < argc; ++index) {
        const std::string argument(argv[index]);
        if (argument == "--ipc") {
            result->binary_ipc = true;
        } else if (argument == "--frame-pool" && index + 1 < argc) {
            result->frame_pool_path = argv[++index];
        } else if (argument == "--help" || argument == "-h") {
            std::cout
                << "bomaxing-driver-host [--ipc] [--frame-pool <path>]\n";
            return false;
        } else {
            std::cerr << "Unknown argument: " << argument << '\n';
            return false;
        }
    }
    return true;
}

}  // namespace

int main(int argc, char** argv) {
#if defined(_WIN32)
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif

    options parsed;
    if (!parse_options(argc, argv, &parsed)) {
        return argc > 1 && (std::string(argv[1]) == "--help" ||
                            std::string(argv[1]) == "-h")
            ? 0
            : 2;
    }

    std::unique_ptr<bomaxing::shared_frame_pool> frame_pool;
    if (!parsed.frame_pool_path.empty()) {
        frame_pool = std::make_unique<bomaxing::shared_frame_pool>(
            parsed.frame_pool_path,
            8u,
            16u * 1024u * 1024u);
        if (!frame_pool->valid()) {
            std::cerr << "Unable to create shared frame pool.\n";
            return 3;
        }
    }

    if (parsed.binary_ipc) {
        return run_binary_protocol(frame_pool.get()) ? 0 : 4;
    }

    run_line_protocol(frame_pool.get());
    return 0;
}
