// kimodo-runner: wraps the kimodo.cpp C API (include/kimodo/kimodo_capi.h, ABI 1) as a separate
// process for Blazma Animation (DECISIONS D6). Protocol: newline-delimited JSON-RPC 2.0 on
// stdin/stdout, one UTF-8 JSON object per line; stdout carries only protocol messages, logs go
// to stderr. Methods: hello, diagnose, generate; notifications from Blazma: cancel, shutdown;
// hello and diagnose list the GGML devices the loaded backends offer (like llama.cpp --list-devices);
// generate takes device = auto | gpu | cpu | hybrid (text encoder on the CPU, motion model on the GPU)
// and an optional gpu = <device name>, and reports the devices it actually ran on (DECISIONS D43).
// notifications to Blazma: progress {id, stage, fraction, message}.
//
// This program never downloads anything and never opens a network connection. It loads only
// the model files whose paths Blazma passes (chosen by the user).
//
// Lifetime (DECISIONS D29): stdin is read on its own thread, so the runner notices at once when
// Blazma goes away, even in the middle of a generation that kimodo.cpp cannot interrupt:
//  * end of stdin (Blazma exited, crashed or was killed: the pipe closes) -> std::_Exit;
//  * "cancel" while a generation runs -> std::_Exit (Blazma also kills the process);
//  * Linux: when Blazma passes KIMODO_RUNNER_PARENT_PID, PR_SET_PDEATHSIG(SIGKILL) as a second
//    line of defence (Blazma starts the runner from a thread that lives as long as the host,
//    because the signal fires when the creating *thread* exits);
//  * Windows: Blazma puts the runner in a Job object with KILL_ON_JOB_CLOSE.
// Requests are read with a hard size limit: an oversized line is skipped without buffering it.

#include <kimodo/kimodo_capi.h>
#if defined(KIMODO_RUNNER_HAVE_GGML)
#include <ggml-backend.h>
#endif
#include <nlohmann/json.hpp>

#include "soma_skeleton.hpp"

#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <filesystem>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#endif
#if defined(__linux__)
#include <csignal>
#include <sys/prctl.h>
#include <unistd.h>
#endif

namespace {

using json = nlohmann::json;
constexpr int protocol_version = 1;
constexpr const char *runner_version = "0.3.0";
constexpr std::size_t max_request_bytes = 1u << 20; // requests are small (a prompt and paths)
constexpr std::int64_t max_frames = 10000;
constexpr std::int64_t max_steps = 1000;
constexpr std::int64_t max_threads = 1024;

struct model_deleter { void operator()(kimodo_model *m) const noexcept { kimodo_model_free(m); } };
struct motion_deleter { void operator()(kimodo_motion *m) const noexcept { kimodo_motion_free(m); } };
using model_ptr = std::unique_ptr<kimodo_model, model_deleter>;
using motion_ptr = std::unique_ptr<kimodo_motion, motion_deleter>;

struct loaded_model {
    model_ptr model;
    std::string motion_path, text_path, text_device, motion_device;
    std::int64_t threads = 0;
};
loaded_model g_model;

struct rpc_error {
    std::string code;
    std::string message;
};

std::mutex g_out_mutex;

void send(const json &message) {
    // One line per message; dump() escapes control characters, so the message never contains '\n'.
    const std::string line = message.dump(-1, ' ', false, json::error_handler_t::replace) + "\n";
    std::lock_guard<std::mutex> lock(g_out_mutex);
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fflush(stdout);
}

void log(const std::string &text) {
    const std::string line = text + "\n";
    std::fwrite(line.data(), 1, line.size(), stderr);
    std::fflush(stderr);
}

[[noreturn]] void hard_exit(int code, const std::string &why) {
    log(why);
    std::fflush(stdout);
    std::_Exit(code); // no destructors: a generation may be running on the main thread
}

void send_error(const json &id, int code, const std::string &message, const std::string &data_code) {
    send({{"jsonrpc", "2.0"}, {"id", id}, {"error", {{"code", code}, {"message", message}, {"data", {{"code", data_code}}}}}});
}

void progress(const json &id, const char *stage, double fraction, const std::string &message = {}) {
    json p{{"id", id}, {"stage", stage}, {"fraction", fraction}};
    if (!message.empty()) p["message"] = message;
    send({{"jsonrpc", "2.0"}, {"method", "progress"}, {"params", p}});
}

void set_env(const char *name, const std::string &value) {
#if defined(_WIN32)
    _putenv_s(name, value.c_str()); // an empty value removes the variable
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

// ---------------------------------------------------------------- strict parameter reading

// Integers only (no floats, booleans or strings), read as 64-bit and range-checked before any
// narrowing, so 4294967396 cannot wrap to 100.
std::int64_t get_int(const json &p, const char *key, std::int64_t fallback, std::int64_t lo, std::int64_t hi) {
    if (!p.contains(key) || p[key].is_null()) return fallback;
    const json &v = p[key];
    const auto range = [&] {
        return rpc_error{"invalid_request", std::string(key) + " must be an integer in " + std::to_string(lo) + ".." + std::to_string(hi)};
    };
    if (!v.is_number_integer()) throw range();
    if (v.is_number_unsigned()) {
        const auto u = v.get<std::uint64_t>();
        if (u > static_cast<std::uint64_t>(hi)) throw range();
        return static_cast<std::int64_t>(u);
    }
    const auto s = v.get<std::int64_t>();
    if (s < lo || s > hi) throw range();
    return s;
}

std::uint64_t get_seed(const json &p) {
    if (!p.contains("seed") || p["seed"].is_null()) return 0;
    const json &v = p["seed"];
    if (!v.is_number_integer()) throw rpc_error{"invalid_request", "seed must be a non-negative integer"};
    if (v.is_number_unsigned()) return v.get<std::uint64_t>();
    const auto s = v.get<std::int64_t>();
    if (s < 0) throw rpc_error{"invalid_request", "seed must be a non-negative integer"};
    return static_cast<std::uint64_t>(s);
}

std::string get_string(const json &p, const char *key, const std::string &fallback) {
    if (!p.contains(key) || p[key].is_null()) return fallback;
    if (!p[key].is_string()) throw rpc_error{"invalid_request", std::string(key) + " must be a string"};
    return p[key].get<std::string>();
}

std::string require_file(const json &p, const char *key) {
    if (!p.contains(key) || !p[key].is_string()) throw rpc_error{"invalid_request", std::string("missing '") + key + "'"};
    const auto path = p[key].get<std::string>();
    std::error_code ec;
    if (!std::filesystem::is_regular_file(utf8_path(path), ec))
        throw rpc_error{"models_not_found", std::string(key) + " not found: " + path};
    return path;
}

// ---------------------------------------------------------------- devices
// The runner and kimodo.cpp share one GGML backend registry (same ggml library in the process), so
// the devices listed here are exactly the ones kimodo.cpp can be told to use (KIMODO_TEXT_DEVICE /
// KIMODO_MOTION_DEVICE, see ai/kimodo/patches/kimodo_backend_select.hpp).
#if defined(KIMODO_RUNNER_HAVE_GGML)
void load_backends() {
    static std::once_flag once;
    // GGML_BACKEND_DL: load every ggml-<backend> library next to the program; a backend whose own
    // dependencies are missing (no Vulkan loader, no NVIDIA driver) is skipped, the CPU always loads.
    std::call_once(once, [] { if (ggml_backend_dev_count() == 0) ggml_backend_load_all(); });
}
const char *type_name(enum ggml_backend_dev_type t) {
    switch (t) {
    case GGML_BACKEND_DEVICE_TYPE_CPU: return "cpu";
    case GGML_BACKEND_DEVICE_TYPE_GPU: return "gpu";
    case GGML_BACKEND_DEVICE_TYPE_IGPU: return "igpu";
    default: return "accel";
    }
}
std::string backend_of(ggml_backend_dev_t d) { return ggml_backend_reg_name(ggml_backend_dev_backend_reg(d)); }
bool is_gpu(ggml_backend_dev_t d) {
    const auto t = ggml_backend_dev_type(d);
    return t == GGML_BACKEND_DEVICE_TYPE_GPU || t == GGML_BACKEND_DEVICE_TYPE_IGPU;
}
json device_json(ggml_backend_dev_t d) {
    std::size_t free = 0, total = 0;
    ggml_backend_dev_memory(d, &free, &total);
    return {{"name", ggml_backend_dev_name(d)}, {"description", ggml_backend_dev_description(d)},
            {"type", type_name(ggml_backend_dev_type(d))}, {"backend", backend_of(d)},
            {"memoryTotal", total}, {"memoryFree", free}};
}
json devices_json() {
    load_backends();
    json list = json::array();
    for (std::size_t i = 0; i < ggml_backend_dev_count(); ++i) {
        // ACCEL devices (BLAS and the like) are helpers of the CPU, not a place to run a model.
        auto *d = ggml_backend_dev_get(i);
        if (ggml_backend_dev_type(d) != GGML_BACKEND_DEVICE_TYPE_ACCEL) list.push_back(device_json(d));
    }
    return list;
}
json backends_json() {
    load_backends();
    json list = json::array();
    for (std::size_t i = 0; i < ggml_backend_reg_count(); ++i) list.push_back(ggml_backend_reg_name(ggml_backend_reg_get(i)));
    return list;
}
// GPU preference, the same order as Blazma's Auto: CUDA, then other discrete GPUs (Vulkan), then integrated.
ggml_backend_dev_t pick_gpu(const std::string &wanted, const std::string &only_backend) {
    load_backends();
    if (!wanted.empty()) {
        auto *d = ggml_backend_dev_by_name(wanted.c_str());
        if (!d || !is_gpu(d)) throw rpc_error{"device_unavailable", "GPU device '" + wanted + "' is not available"};
        return d;
    }
    ggml_backend_dev_t best = nullptr;
    int best_rank = 100;
    for (std::size_t i = 0; i < ggml_backend_dev_count(); ++i) {
        auto *d = ggml_backend_dev_get(i);
        if (!is_gpu(d)) continue;
        const auto backend = backend_of(d);
        if (!only_backend.empty() && backend != only_backend) continue;
        const int rank = ggml_backend_dev_type(d) == GGML_BACKEND_DEVICE_TYPE_IGPU ? 2 : backend == "CUDA" ? 0 : 1;
        if (rank < best_rank) { best = d; best_rank = rank; }
    }
    return best;
}
ggml_backend_dev_t cpu_device() {
    load_backends();
    auto *d = ggml_backend_dev_by_type(GGML_BACKEND_DEVICE_TYPE_CPU);
    if (!d) throw rpc_error{"device_unavailable", "no GGML CPU backend could be loaded (ggml-cpu library missing next to kimodo-runner?)"};
    return d;
}
#endif
struct device_plan {
    std::string mode;          // the resolved mode: gpu, cpu or hybrid
    std::string text, motion;  // GGML device names
    json text_json, motion_json;
};
device_plan plan_devices(const std::string &device, const std::string &gpu) {
#if defined(KIMODO_RUNNER_HAVE_GGML)
    ggml_backend_dev_t g = nullptr;
    if (device != "cpu") {
        g = pick_gpu(gpu, device == "vulkan" ? "Vulkan" : "");
        if (!g && device != "auto")
            throw rpc_error{"device_unavailable", "no usable GPU was found (no CUDA or Vulkan device; check the graphics driver)"};
    }
    auto *c = cpu_device();
    std::string mode = !g ? "cpu" : device == "hybrid" ? "hybrid" : "gpu";
    auto *text = mode == "gpu" ? g : c;
    auto *motion = mode == "cpu" ? c : g;
    return {mode, ggml_backend_dev_name(text), ggml_backend_dev_name(motion), device_json(text), device_json(motion)};
#else
    (void)device; (void)gpu;
    throw rpc_error{"device_unavailable", "kimodo.cpp was built without GGML"};
#endif
}
// ---------------------------------------------------------------- methods

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

json device_info(json r) {
#if defined(KIMODO_RUNNER_HAVE_GGML)
    r["backends"] = backends_json();
    r["devices"] = devices_json();
#else
    r["backends"] = json::array();
    r["devices"] = json::array();
#endif
    r["deviceModes"] = {"auto", "gpu", "cpu", "hybrid"};
    return r;
}
json hello(const json &) {
    return device_info({{"protocol", protocol_version}, {"runner", "kimodo-runner"}, {"version", runner_version},
            {"kimodoAbi", kimodo_abi_version()}, {"skeletons", {std::string(blazma::soma::key)}},
            {"ggml", ggml_compiled()}, {"vulkanCompiled", vulkan_compiled()}});
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
    r = device_info(std::move(r));
    if (r["devices"].empty()) r["warnings"].push_back("no GGML device could be loaded (ggml-cpu library missing?)");
    return r;
}

kimodo_model *ensure_model(const json &id, const std::string &motion, const std::string &text, const device_plan &plan, std::int64_t threads) {
    // kimodo.cpp (with Blazma's patch) reads KIMODO_TEXT_DEVICE when the model loads and
    // KIMODO_MOTION_DEVICE when the motion weights load at the first generation, plus KIMODO_THREADS;
    // all are set for every request (nothing leaks from an earlier one) and are part of the cache key.
    set_env("KIMODO_BACKEND", "");
    set_env("KIMODO_TEXT_DEVICE", plan.text);
    set_env("KIMODO_MOTION_DEVICE", plan.motion);
    set_env("KIMODO_THREADS", threads > 0 ? std::to_string(threads) : "");
    if (g_model.model && g_model.motion_path == motion && g_model.text_path == text && g_model.text_device == plan.text &&
        g_model.motion_device == plan.motion && g_model.threads == threads)
        return g_model.model.get();
    g_model.model.reset();
    progress(id, "load_model", 0.05, "loading Kimodo models");
    log("loading motion model " + motion + " on " + plan.motion + " and text model " + text + " on " + plan.text);
    kimodo_runtime_options options{};
    options.size = sizeof(options);
    options.device = plan.mode == "cpu" ? KIMODO_DEVICE_CPU : KIMODO_DEVICE_AUTO; // informational in ABI 1
    options.threads = static_cast<std::uint32_t>(threads);
    char err[1024] = {0};
    auto *m = kimodo_model_load(motion.c_str(), text.c_str(), nullptr, &options, err, static_cast<int>(sizeof(err)));
    if (!m) throw rpc_error{"model_load_failed", std::string("could not load the Kimodo model: ") + err};
    g_model.model.reset(m);
    g_model.motion_path = motion;
    g_model.text_path = text;
    g_model.text_device = plan.text;
    g_model.motion_device = plan.motion;
    g_model.threads = threads;
    return m;
}

std::atomic<bool> g_busy{false}; // a generation is running on the main thread
std::atomic<bool> g_eof{false};   // stdin reached end of input

json generate(const json &id, const json &p) {
    const auto prompt = get_string(p, "prompt", "");
    if (prompt.empty()) throw rpc_error{"invalid_request", "missing prompt"};
    if (prompt.size() > 4000) throw rpc_error{"invalid_request", "prompt too long"};
    const auto frames = get_int(p, "frames", -1, 1, max_frames);
    if (frames < 1) throw rpc_error{"invalid_request", "missing frames"};
    const auto steps = get_int(p, "steps", 100, 1, max_steps);
    const auto threads = get_int(p, "threads", 0, 0, max_threads);
    const auto seed = get_seed(p);
    const auto device = get_string(p, "device", "auto");
    // "vulkan" is the 0.2 runner's value, kept for older Blazma builds: GPU mode limited to Vulkan devices.
    if (device != "auto" && device != "gpu" && device != "cpu" && device != "hybrid" && device != "vulkan")
        throw rpc_error{"invalid_request", "device must be auto, gpu, cpu or hybrid"};
    const auto gpu = get_string(p, "gpu", "");
    if (gpu.size() > 64) throw rpc_error{"invalid_request", "gpu must be a device name such as CUDA0 or Vulkan0"};
    const auto motion_path = require_file(p, "motionModel");
    const auto text_path = require_file(p, "textModel");
    // Blazma is gone: do not start a long generation for nobody (the request was still validated above).
    if (g_eof.load()) throw rpc_error{"input_closed", "input closed before the generation started"};

    const auto plan = plan_devices(device, gpu);
    auto *model = ensure_model(id, motion_path, text_path, plan, threads);
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
    if (t <= 0 || t > max_frames || !rot || !pos) throw rpc_error{"generation_failed", "Kimodo returned an empty or oversized motion"};

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
            {"runner", std::string("kimodo-runner ") + runner_version}, {"warnings", json::array()},
            {"device", {{"mode", plan.mode}, {"text", plan.text_json}, {"motion", plan.motion_json}}}};
}

// ---------------------------------------------------------------- input thread

struct input_item {
    enum class kind { message, parse_error, oversize, eof } k;
    json message;     // kind::message
    json id;          // kind::parse_error: the request id when it could be recovered, else null
};

std::mutex g_queue_mutex;
std::condition_variable g_queue_cv;
std::deque<input_item> g_queue;

void push(input_item item) {
    {
        std::lock_guard<std::mutex> lock(g_queue_mutex);
        g_queue.push_back(std::move(item));
    }
    g_queue_cv.notify_one();
}

// Best effort: the numeric "id" of a request that is not valid JSON, so its error can name it.
json recover_id(const std::string &line) {
    const auto at = line.find("\"id\"");
    if (at == std::string::npos) return nullptr;
    std::size_t i = at + 4;
    while (i < line.size() && (line[i] == ' ' || line[i] == '\t')) ++i;
    if (i >= line.size() || line[i] != ':') return nullptr;
    ++i;
    while (i < line.size() && (line[i] == ' ' || line[i] == '\t')) ++i;
    std::size_t start = i;
    while (i < line.size() && i - start < 18 && line[i] >= '0' && line[i] <= '9') ++i;
    if (i == start || i - start >= 18) return nullptr;
    return std::stoll(line.substr(start, i - start));
}

void handle_input_line(std::string &line) {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    if (line.empty()) return;
    json msg = json::parse(line, nullptr, false);
    if (msg.is_discarded() || !msg.is_object()) {
        push({input_item::kind::parse_error, nullptr, recover_id(line)});
        return;
    }
    const bool notification = !msg.contains("id");
    if (notification && msg.contains("method") && msg["method"].is_string()) {
        const auto method = msg["method"].get<std::string>();
        // Acted on here, not queued: the main thread may be blocked inside kimodo_generate.
        if (method == "cancel" && g_busy.load()) hard_exit(130, "cancel received during a generation: exiting");
        if (method == "cancel") return;
    }
    push({input_item::kind::message, std::move(msg), nullptr});
}

void read_input() {
    std::string line;
    line.reserve(4096);
    bool overflow = false;
    for (;;) {
        const int c = std::fgetc(stdin);
        if (c == EOF) {
            // Blazma is gone (or closed our input): never keep computing for nobody. g_eof is set before
            // g_busy is read, and the main thread sets g_busy before reading g_eof, so one of them sees the other.
            g_eof.store(true);
            if (g_busy.load()) hard_exit(0, "stdin closed during a generation: exiting");
            push({input_item::kind::eof, nullptr, nullptr});
            return;
        }
        if (c == '\n') {
            if (overflow) push({input_item::kind::oversize, nullptr, nullptr});
            else handle_input_line(line);
            line.clear();
            overflow = false;
            continue;
        }
        if (overflow) continue; // skip the rest of an oversized line without storing it
        if (line.size() >= max_request_bytes) {
            overflow = true;
            std::string().swap(line);
            continue;
        }
        line.push_back(static_cast<char>(c));
    }
}

input_item pop() {
    std::unique_lock<std::mutex> lock(g_queue_mutex);
    g_queue_cv.wait(lock, [] { return !g_queue.empty(); });
    auto item = std::move(g_queue.front());
    g_queue.pop_front();
    return item;
}

#if defined(__linux__)
void bind_to_parent() {
    const char *v = std::getenv("KIMODO_RUNNER_PARENT_PID");
    if (!v || !*v) return;
    char *end = nullptr;
    const long expected = std::strtol(v, &end, 10);
    if (end == v || *end != '\0' || expected <= 1) return;
    if (prctl(PR_SET_PDEATHSIG, SIGKILL) != 0) { log("prctl(PR_SET_PDEATHSIG) failed"); return; }
    // The parent may have died before prctl took effect (then we were re-parented): check after setting it.
    if (getppid() != static_cast<pid_t>(expected)) {
        // Either Blazma is already gone, or a wrapper process sits in between; stdin EOF covers both.
        log("parent process is not the one Blazma announced; relying on stdin EOF only");
        prctl(PR_SET_PDEATHSIG, 0);
    }
}
#endif

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
#if defined(__linux__)
    bind_to_parent();
#endif
    log(std::string("kimodo-runner ") + runner_version + " ready (protocol " + std::to_string(protocol_version) + ")");
    std::thread(read_input).detach();

    for (;;) {
        auto item = pop();
        switch (item.k) {
        case input_item::kind::eof:
            log("stdin closed: exiting");
            return 0;
        case input_item::kind::oversize:
            // The id cannot be known without buffering the line; Blazma fails its in-flight call on id:null errors.
            send_error(nullptr, -32600, "request too large", "invalid_request");
            continue;
        case input_item::kind::parse_error:
            send_error(item.id, -32700, "parse error", "protocol_error");
            continue;
        case input_item::kind::message:
            break;
        }
        const json &msg = item.message;
        const json id = msg.contains("id") ? msg["id"] : json(nullptr);
        if (!msg.contains("method") || !msg["method"].is_string()) {
            if (msg.contains("id")) send_error(id, -32600, "invalid request: no method", "protocol_error");
            else send_error(nullptr, -32600, "invalid request: no method", "protocol_error");
            continue;
        }
        const auto method = msg["method"].get<std::string>();
        const json params = msg.contains("params") && msg["params"].is_object() ? msg["params"] : json::object();
        if (!msg.contains("id")) { // notifications ("cancel" is handled on the input thread)
            if (method == "shutdown") { log("shutdown"); return 0; }
            continue;
        }
        try {
            json result;
            if (method == "hello") result = hello(params);
            else if (method == "diagnose") result = diagnose(params);
            else if (method == "generate") {
                g_busy.store(true);
                struct clear_busy { ~clear_busy() { g_busy.store(false); } } guard;
                result = generate(id, params);
            } else throw rpc_error{"unknown_method", "unknown method '" + method + "'"};
            send({{"jsonrpc", "2.0"}, {"id", id}, {"result", std::move(result)}});
        } catch (const rpc_error &e) {
            log("error [" + e.code + "]: " + e.message);
            send_error(id, -32000, e.message, e.code);
        } catch (const std::exception &e) {
            log(std::string("internal error: ") + e.what());
            send_error(id, -32603, e.what(), "internal_error");
        }
    }
}
