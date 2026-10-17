// kimodo-runner: wraps the kimodo.cpp C API (include/kimodo/kimodo_capi.h, ABI 1) as a separate
// process for Blazma Animation (DECISIONS D6). Protocol: newline-delimited JSON-RPC 2.0 on
// stdin/stdout, one UTF-8 JSON object per line; stdout carries only protocol messages, logs go
// to stderr. Methods: hello, diagnose, generate; notifications from Blazma: cancel, shutdown;
// notifications to Blazma: progress {id, stage, fraction, message}.
//
// This program never downloads anything and never opens a network connection. It loads only
// the model files whose paths Blazma passes (chosen by the user).
//
// kimodo_generate blocks and cannot be interrupted, so "cancel" is acknowledged by Blazma
// killing this process; the runner itself does nothing for it.

#include <kimodo/kimodo_capi.h>
#include <nlohmann/json.hpp>

#include "soma_skeleton.hpp"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <memory>
#include <string>

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#endif

namespace {

using json = nlohmann::json;
constexpr int protocol_version = 1;
constexpr const char *runner_version = "0.1.0";
constexpr std::size_t max_request_bytes = 1u << 20; // requests are small (a prompt and paths)

struct model_deleter { void operator()(kimodo_model *m) const noexcept { kimodo_model_free(m); } };
struct motion_deleter { void operator()(kimodo_motion *m) const noexcept { kimodo_motion_free(m); } };
using model_ptr = std::unique_ptr<kimodo_model, model_deleter>;
using motion_ptr = std::unique_ptr<kimodo_motion, motion_deleter>;

struct loaded_model {
    model_ptr model;
    std::string motion_path, text_path, device;
};
loaded_model g_model;

struct rpc_error {
    std::string code;
    std::string message;
};

void send(const json &message) {
    // One line per message; dump() escapes control characters, so the message never contains '\n'.
    const std::string line = message.dump(-1, ' ', false, json::error_handler_t::replace) + "\n";
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fflush(stdout);
}

void log(const std::string &text) { std::cerr << text << '\n' << std::flush; }

void progress(const json &id, const char *stage, double fraction, const std::string &message = {}) {
    json p{{"id", id}, {"stage", stage}, {"fraction", fraction}};
    if (!message.empty()) p["message"] = message;
    send({{"jsonrpc", "2.0"}, {"method", "progress"}, {"params", p}});
}

void set_env(const char *name, const std::string &value) {
#if defined(_WIN32)
    _putenv_s(name, value.c_str());
#else
    if (value.empty()) unsetenv(name); else setenv(name, value.c_str(), 1);
#endif
}

bool vulkan_compiled() {
#if defined(KIMODO_RUNNER_HAVE_VULKAN)
    return true;
#else
    return false;
#endif
}

bool ggml_compiled() {
#if defined(KIMODO_RUNNER_HAVE_GGML)
    return true;
#else
    return false;
#endif
}

std::filesystem::path utf8_path(const std::string &s) {
    return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t *>(s.data()), s.size()));
}

std::string require_file(const json &p, const char *key) {
    if (!p.contains(key) || !p[key].is_string()) throw rpc_error{"invalid_request", std::string("missing '") + key + "'"};
    const auto path = p[key].get<std::string>();
    std::error_code ec;
    if (!std::filesystem::is_regular_file(utf8_path(path), ec))
        throw rpc_error{"models_not_found", std::string(key) + " not found: " + path};
    return path;
}

json skeleton_json() {
    json names = json::array(), parents = json::array(), offsets = json::array();
    for (int j = 0; j < blazma::soma::joints; ++j) {
        names.push_back(std::string(blazma::soma::names[j]));
        parents.push_back(blazma::soma::parents[j]);
        const auto &o = blazma::soma::offsets[j];
        offsets.push_back({o[0], o[1], o[2]});
    }
    return {{"key", std::string(blazma::soma::key)}, {"up", "+Y"}, {"forward", "+Z"},
            {"names", names}, {"parents", parents}, {"offsets", offsets}};
}

json hello(const json &) {
    return {{"protocol", protocol_version}, {"runner", "kimodo-runner"}, {"version", runner_version},
            {"kimodoAbi", kimodo_abi_version()}, {"skeletons", {std::string(blazma::soma::key)}},
            {"ggml", ggml_compiled()}, {"vulkanCompiled", vulkan_compiled()}};
}

json diagnose(const json &p) {
    json warnings = json::array();
    if (!ggml_compiled()) warnings.push_back("kimodo.cpp was built without GGML: generation is unavailable.");
    if (kimodo_abi_version() != KIMODO_CAPI_ABI_VERSION) warnings.push_back("kimodo C ABI version mismatch.");
    std::error_code ec;
    auto exists = [&](const char *k) {
        return p.contains(k) && p[k].is_string() && std::filesystem::is_regular_file(utf8_path(p[k].get<std::string>()), ec);
    };
    json r{{"ggml", ggml_compiled()}, {"vulkanCompiled", vulkan_compiled()}, {"kimodoAbi", kimodo_abi_version()},
           {"motionModelExists", exists("motionModel")}, {"textModelExists", exists("textModel")},
           {"modelLoaded", static_cast<bool>(g_model.model)}, {"warnings", warnings}};
    if (const char *b = std::getenv("KIMODO_BACKEND")) r["KIMODO_BACKEND"] = b;
    if (const char *t = std::getenv("KIMODO_THREADS")) r["KIMODO_THREADS"] = t;
    return r;
}

kimodo_model *ensure_model(const json &id, const std::string &motion, const std::string &text, const std::string &device) {
    if (g_model.model && g_model.motion_path == motion && g_model.text_path == text && g_model.device == device)
        return g_model.model.get();
    g_model.model.reset();
    if (device == "vulkan" && !vulkan_compiled()) throw rpc_error{"device_unavailable", "this kimodo-runner was built without Vulkan"};
    // kimodo.cpp selects its backend from the environment (KIMODO_BACKEND=cpu forces CPU);
    // kimodo_runtime_options is validated but not used by the reviewed revision.
    set_env("KIMODO_BACKEND", device == "cpu" ? "cpu" : "");
    progress(id, "load_model", 0.05, "loading Kimodo models");
    log("loading motion model " + motion + " and text model " + text + " (device " + device + ")");
    kimodo_runtime_options options{};
    options.size = sizeof(options);
    options.device = device == "cpu" ? KIMODO_DEVICE_CPU : device == "vulkan" ? KIMODO_DEVICE_VULKAN : KIMODO_DEVICE_AUTO;
    char err[1024] = {0};
    auto *m = kimodo_model_load(motion.c_str(), text.c_str(), nullptr, &options, err, static_cast<int>(sizeof(err)));
    if (!m) throw rpc_error{"model_load_failed", std::string("could not load the Kimodo model: ") + err};
    g_model = {model_ptr(m), motion, text, device};
    return m;
}

json generate(const json &id, const json &p) {
    if (!p.contains("prompt") || !p["prompt"].is_string() || p["prompt"].get<std::string>().empty())
        throw rpc_error{"invalid_request", "missing prompt"};
    const auto prompt = p["prompt"].get<std::string>();
    if (prompt.size() > 4000) throw rpc_error{"invalid_request", "prompt too long"};
    const auto frames = p.value("frames", 0);
    if (frames < 1 || frames > 10000) throw rpc_error{"invalid_request", "frames must be in 1..10000"};
    const auto steps = p.value("steps", 100);
    if (steps < 1 || steps > 1000) throw rpc_error{"invalid_request", "steps must be in 1..1000"};
    const auto seed = p.value("seed", static_cast<std::uint64_t>(0));
    const auto device = p.value("device", std::string("auto"));
    if (device != "auto" && device != "cpu" && device != "vulkan") throw rpc_error{"invalid_request", "device must be auto, cpu or vulkan"};
    if (p.value("threads", 0) > 0) set_env("KIMODO_THREADS", std::to_string(p.value("threads", 0)));
    const auto motion_path = require_file(p, "motionModel");
    const auto text_path = require_file(p, "textModel");

    auto *model = ensure_model(id, motion_path, text_path, device);
    progress(id, "generate", 0.2, "generating motion");
    kimodo_generation_options o{};
    o.size = sizeof(o);
    o.seed = seed;
    o.frames = static_cast<std::uint32_t>(frames);
    o.diffusion_steps = static_cast<std::uint32_t>(steps);
    o.text_cfg_weight = 2.0F;       // the values kimodo.cpp's kmd-generate uses
    o.constraint_cfg_weight = 2.0F;
    char err[1024] = {0};
    motion_ptr motion(kimodo_generate(model, prompt.c_str(), &o, err, static_cast<int>(sizeof(err))));
    if (!motion) throw rpc_error{"generation_failed", std::string("Kimodo generation failed: ") + err};

    progress(id, "decode", 0.95, "sending motion");
    const int t = kimodo_motion_frames(motion.get());
    const int j = kimodo_motion_joints(motion.get());
    if (j != blazma::soma::joints)
        throw rpc_error{"unsupported_skeleton", "the model predicts " + std::to_string(j) + " joints; only the 30-joint SOMA models are supported"};
    const float *rot = kimodo_motion_local_rotations_xyzw(motion.get());
    const float *pos = kimodo_motion_root_positions(motion.get());
    if (t <= 0 || !rot || !pos) throw rpc_error{"generation_failed", "Kimodo returned an empty motion"};

    json rotations = json::array(), roots = json::array();
    rotations.get_ref<json::array_t &>().reserve(static_cast<std::size_t>(t) * j * 4);
    for (std::size_t i = 0; i < static_cast<std::size_t>(t) * j * 4; ++i) {
        if (!std::isfinite(rot[i])) throw rpc_error{"generation_failed", "Kimodo returned a non-finite rotation"};
        rotations.push_back(rot[i]);
    }
    for (std::size_t i = 0; i < static_cast<std::size_t>(t) * 3; ++i) {
        if (!std::isfinite(pos[i])) throw rpc_error{"generation_failed", "Kimodo returned a non-finite root position"};
        roots.push_back(pos[i]);
    }
    return {{"fps", blazma::soma::fps}, {"frames", t}, {"joints", j}, {"skeleton", skeleton_json()},
            {"rotations", std::move(rotations)}, {"rootPositions", std::move(roots)},
            {"model", motion_path}, {"textModel", text_path}, {"steps", steps}, {"seed", seed},
            {"runner", std::string("kimodo-runner ") + runner_version}, {"warnings", json::array()}};
}

} // namespace

int main(int argc, char **argv) {
    bool stdio = false;
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        if (a == "--stdio") stdio = true;
        else if (a == "--version") { std::printf("kimodo-runner %s (protocol %d, kimodo ABI %d)\n", runner_version, protocol_version, kimodo_abi_version()); return 0; }
    }
    if (!stdio) {
        std::fprintf(stderr, "usage: kimodo-runner --stdio | --version\n"
                             "Started by Blazma Animation; speaks newline-delimited JSON-RPC on stdin/stdout.\n");
        return 2;
    }
#if defined(_WIN32)
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stdin), _O_BINARY);
#endif
    log(std::string("kimodo-runner ") + runner_version + " ready (protocol " + std::to_string(protocol_version) + ")");

    std::string line;
    while (std::getline(std::cin, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;
        if (line.size() > max_request_bytes) {
            send({{"jsonrpc", "2.0"}, {"id", nullptr}, {"error", {{"code", -32600}, {"message", "request too large"}, {"data", {{"code", "invalid_request"}}}}}});
            continue;
        }
        json msg = json::parse(line, nullptr, false);
        if (msg.is_discarded() || !msg.is_object() || !msg.contains("method") || !msg["method"].is_string()) {
            send({{"jsonrpc", "2.0"}, {"id", nullptr}, {"error", {{"code", -32700}, {"message", "parse error"}, {"data", {{"code", "protocol_error"}}}}}});
            continue;
        }
        const auto method = msg["method"].get<std::string>();
        const json params = msg.contains("params") && msg["params"].is_object() ? msg["params"] : json::object();
        if (!msg.contains("id")) { // notifications
            if (method == "shutdown") { log("shutdown"); return 0; }
            continue; // "cancel": the client stops this process
        }
        const json id = msg["id"];
        try {
            json result;
            if (method == "hello") result = hello(params);
            else if (method == "diagnose") result = diagnose(params);
            else if (method == "generate") result = generate(id, params);
            else throw rpc_error{"unknown_method", "unknown method '" + method + "'"};
            send({{"jsonrpc", "2.0"}, {"id", id}, {"result", std::move(result)}});
        } catch (const rpc_error &e) {
            log("error [" + e.code + "]: " + e.message);
            send({{"jsonrpc", "2.0"}, {"id", id}, {"error", {{"code", -32000}, {"message", e.message}, {"data", {{"code", e.code}}}}}});
        } catch (const std::exception &e) {
            log(std::string("internal error: ") + e.what());
            send({{"jsonrpc", "2.0"}, {"id", id}, {"error", {{"code", -32603}, {"message", e.what()}, {"data", {{"code", "internal_error"}}}}}});
        }
    }
    return 0;
}
