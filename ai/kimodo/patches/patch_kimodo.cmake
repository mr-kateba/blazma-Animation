# Applies Blazma's small kimodo.cpp patch (device selection through the GGML backend registry, see
# kimodo_backend_select.hpp) to a kimodo.cpp source tree. Run with: cmake -DSRC=<tree> -P patch_kimodo.cmake
# Plain string replacement, no patch/git tools needed; idempotent; fails loudly if upstream text moved.
cmake_minimum_required(VERSION 3.25)
if(NOT SRC OR NOT EXISTS "${SRC}/src/ggml_weights.cpp")
  message(FATAL_ERROR "patch_kimodo.cmake: SRC must be a kimodo.cpp source tree")
endif()
file(COPY "${CMAKE_CURRENT_LIST_DIR}/kimodo_backend_select.hpp" DESTINATION "${SRC}/src")

# Replace the text between `begin` and `end` (end included) in `file`; a no-op once `marker` is present.
function(replace_region file begin end replacement marker)
  file(READ "${SRC}/${file}" text)
  string(REPLACE "\r\n" "\n" text "${text}") # a Windows checkout may have CRLF line ends
  string(FIND "${text}" "${marker}" done)
  if(NOT done EQUAL -1)
    return()
  endif()
  string(FIND "${text}" "${begin}" b)
  if(b EQUAL -1)
    message(FATAL_ERROR "patch_kimodo.cmake: start of the region not found in ${file} (kimodo.cpp changed?)")
  endif()
  string(SUBSTRING "${text}" ${b} -1 tail)
  string(FIND "${tail}" "${end}" e)
  if(e EQUAL -1)
    message(FATAL_ERROR "patch_kimodo.cmake: end of the region not found in ${file} (kimodo.cpp changed?)")
  endif()
  string(LENGTH "${end}" end_len)
  math(EXPR cut "${e} + ${end_len}")
  string(SUBSTRING "${text}" 0 ${b} head)
  string(SUBSTRING "${tail}" ${cut} -1 rest)
  file(WRITE "${SRC}/${file}" "${head}${replacement}${rest}")
endfunction()

function(prepend_include file)
  file(READ "${SRC}/${file}" text)
  string(FIND "${text}" "kimodo_backend_select.hpp" done)
  if(done EQUAL -1)
    file(WRITE "${SRC}/${file}" "#include \"kimodo_backend_select.hpp\" // Blazma patch\n${text}")
  endif()
endfunction()

# Motion model: weights are loaded lazily at the first generation.
prepend_include(src/ggml_weights.cpp)
replace_region(src/ggml_weights.cpp
  "#if defined(KIMODO_HAVE_GGML_VULKAN)\n    // Retain a deterministic CPU escape hatch"
  "ggml_backend_cpu_set_n_threads(result->backend_, cpu_thread_count());\n    }\n"
  "    // Blazma patch: device through the GGML registry (KIMODO_MOTION_DEVICE / KIMODO_BACKEND).
    {
        const char *choice = std::getenv(\"KIMODO_BACKEND\");
        const bool allow_gpu = !choice || std::string_view(choice) != \"cpu\";
        if (allow_gpu) configure_vulkan_f32_parity();
        std::string backend_error;
        result->backend_ = blazma::select_backend(\"KIMODO_MOTION_DEVICE\", allow_gpu, cpu_thread_count(), backend_error);
        if (!result->backend_) return std::unexpected(\"motion model: \" + backend_error);
    }
"
  "KIMODO_MOTION_DEVICE")

# Text encoder: loaded by kimodo_model_load.
prepend_include(src/llm_text_encoder.cpp)
replace_region(src/llm_text_encoder.cpp
  "#if defined(KIMODO_HAVE_GGML_VULKAN)\n    if (use_vulkan()"
  "ggml_backend_cpu_set_n_threads(result->impl_->backend, cpu_thread_count());\n    }\n"
  "    // Blazma patch: device through the GGML registry (KIMODO_TEXT_DEVICE / KIMODO_BACKEND).
    {
        std::string backend_error;
        result->impl_->backend = blazma::select_backend(\"KIMODO_TEXT_DEVICE\", use_vulkan(), cpu_thread_count(), backend_error);
        if (!result->impl_->backend) return std::unexpected(\"text encoder: \" + backend_error);
    }
"
  "KIMODO_TEXT_DEVICE")

# CMake: let the caller choose GGML_BACKEND_DL, and never link a backend MODULE library directly.
replace_region(CMakeLists.txt
  "  set(GGML_BACKEND_DL OFF CACHE BOOL \"\" FORCE)\n"
  "  set(GGML_BACKEND_DL OFF CACHE BOOL \"\" FORCE)\n"
  "  set(GGML_BACKEND_DL OFF CACHE BOOL \"\") # Blazma patch: no FORCE\n"
  "Blazma patch: no FORCE")
replace_region(CMakeLists.txt
  "  if(KIMODO_ENABLE_VULKAN AND TARGET ggml-vulkan)\n    set(KIMODO_VK_LIBS ggml-vulkan)"
  "  if(KIMODO_ENABLE_VULKAN AND TARGET ggml-vulkan)\n"
  "  if(KIMODO_ENABLE_VULKAN AND TARGET ggml-vulkan AND NOT GGML_BACKEND_DL) # Blazma patch\n"
  "AND NOT GGML_BACKEND_DL) # Blazma patch")
message(STATUS "kimodo.cpp patched for registry device selection: ${SRC}")
