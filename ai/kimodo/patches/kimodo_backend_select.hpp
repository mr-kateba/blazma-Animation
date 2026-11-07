// Added to kimodo.cpp by Blazma's build (ai/kimodo/patches/patch_kimodo.cmake); not part of upstream.
//
// Upstream kimodo.cpp (5679ff1) picks its GGML backend with direct calls: ggml_backend_vk_init(0) when
// it was linked with Vulkan, else ggml_backend_cpu_init(). That ties a build to one GPU API and cannot
// work with GGML_BACKEND_DL, where every backend (ggml-cpu-<variant>, ggml-vulkan, ggml-cuda) is a
// library loaded at run time. This helper selects the device through GGML's backend registry instead:
//
//  * <device_variable> (KIMODO_TEXT_DEVICE for the text encoder, KIMODO_MOTION_DEVICE for the motion
//    model) names an exact registry device ("CUDA0", "Vulkan1", "CPU"). If that device is missing or
//    fails to initialise, loading fails: the caller (kimodo-runner / Blazma) decides the fallback.
//  * otherwise, unless KIMODO_BACKEND=cpu (upstream's switch, kept), the first CUDA GPU, else the first
//    other discrete GPU (Vulkan), else an integrated GPU, else the CPU — upstream's "GPU first" default.
//  * a CPU backend gets KIMODO_THREADS threads (upstream's rule, via the registry's set_n_threads).
#pragma once

#include <ggml-backend.h>

#include <cstdlib>
#include <string>
#include <string_view>

namespace kimodo::detail::blazma {

inline ggml_backend_dev_t first_gpu() {
    ggml_backend_dev_t best = nullptr;
    int best_rank = 100;
    for (size_t i = 0; i < ggml_backend_dev_count(); ++i) {
        ggml_backend_dev_t dev = ggml_backend_dev_get(i);
        const auto type = ggml_backend_dev_type(dev);
        const std::string_view reg = ggml_backend_reg_name(ggml_backend_dev_backend_reg(dev));
        int rank = 0;
        if (type == GGML_BACKEND_DEVICE_TYPE_GPU) rank = reg == "CUDA" ? 0 : 1;
        else if (type == GGML_BACKEND_DEVICE_TYPE_IGPU) rank = 2;
        else continue;
        if (rank < best_rank) { best = dev; best_rank = rank; }
    }
    return best;
}

inline ggml_backend_t select_backend(const char *device_variable, bool allow_gpu, int cpu_threads, std::string &error) {
    if (ggml_backend_dev_count() == 0) ggml_backend_load_all(); // GGML_BACKEND_DL: nothing loaded yet
    ggml_backend_dev_t dev = nullptr;
    const char *wanted = std::getenv(device_variable);
    if (wanted && *wanted) {
        dev = ggml_backend_dev_by_name(wanted);
        if (!dev) { error = std::string("GGML device ") + wanted + " is not available"; return nullptr; }
    } else if (allow_gpu) {
        dev = first_gpu();
    }
    if (!dev) dev = ggml_backend_dev_by_type(GGML_BACKEND_DEVICE_TYPE_CPU);
    if (!dev) { error = "no GGML CPU backend could be loaded (ggml-cpu library missing next to the program?)"; return nullptr; }
    ggml_backend_t backend = ggml_backend_dev_init(dev, nullptr);
    if (!backend) { error = std::string("GGML device ") + ggml_backend_dev_name(dev) + " failed to initialise"; return nullptr; }
    if (ggml_backend_dev_type(dev) == GGML_BACKEND_DEVICE_TYPE_CPU) {
        auto *set_threads = reinterpret_cast<ggml_backend_set_n_threads_t>(
            ggml_backend_reg_get_proc_address(ggml_backend_dev_backend_reg(dev), "ggml_backend_set_n_threads"));
        if (set_threads) set_threads(backend, cpu_threads);
    }
    return backend;
}

} // namespace kimodo::detail::blazma
