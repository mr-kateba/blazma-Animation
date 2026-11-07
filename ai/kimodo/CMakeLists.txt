# kimodo-runner: a small process that wraps the kimodo.cpp C API and speaks Blazma's
# newline-delimited JSON-RPC protocol on stdin/stdout (docs/AI_INTEGRATION.md §5).
# kimodo.cpp (Apache-2.0) and its pinned GGML submodule are fetched at configure time;
# nothing is vendored here and no model weights are ever downloaded by this build.
cmake_minimum_required(VERSION 3.25)
project(kimodo_runner LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Reviewed revision of https://github.com/localai-org/kimodo.cpp (2026-09-17, C ABI version 1).
set(KIMODO_CPP_REPOSITORY "https://github.com/localai-org/kimodo.cpp.git" CACHE STRING "kimodo.cpp git repository")
set(KIMODO_CPP_COMMIT "5679ff19ba0a522c0b0516e9a9d402fe1af2c027" CACHE STRING "Pinned kimodo.cpp commit")
# Use a local checkout instead of fetching (it must contain the ggml submodule). It is copied into the
# build tree before patching, so the checkout itself is never modified.
set(KIMODO_CPP_SOURCE_DIR "" CACHE PATH "Local kimodo.cpp checkout (optional)")
option(KIMODO_RUNNER_VULKAN "Build GGML's Vulkan backend (needs the Vulkan headers/loader and glslc)" ON)
option(KIMODO_RUNNER_CUDA "Build GGML's CUDA backend (needs the CUDA toolkit)" OFF)
# One runner, several backends (DECISIONS D43): every GGML backend is a library loaded at run time
# (ggml-cpu-<variant>, ggml-vulkan, ggml-cuda next to the program). A backend whose dependencies are
# missing (no vulkan-1 loader, no NVIDIA driver/CUDA runtime) simply does not load; the CPU always does.
option(KIMODO_RUNNER_BACKEND_DL "Build GGML backends as run-time loadable libraries" ON)

set(KIMODO_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(KIMODO_ENABLE_FUZZERS OFF CACHE BOOL "" FORCE)
set(KIMODO_ENABLE_VULKAN ${KIMODO_RUNNER_VULKAN} CACHE BOOL "" FORCE)
set(GGML_CUDA ${KIMODO_RUNNER_CUDA} CACHE BOOL "" FORCE)
set(GGML_BACKEND_DL ${KIMODO_RUNNER_BACKEND_DL} CACHE BOOL "" FORCE)
if(KIMODO_RUNNER_BACKEND_DL AND CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64|x64)$")
  # One CPU library per instruction-set level (SSE4.2 ... AVX-512); GGML loads the best one the CPU supports.
  set(GGML_CPU_ALL_VARIANTS ON CACHE BOOL "" FORCE)
endif()
# Every program and backend library lands in bin/, where ggml_backend_load_all() looks for them.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

# kimodo.cpp sets BUILD_SHARED_LIBS only after declaring its library; set it first so kimodo is always a
# shared library (kimodo.dll / libkimodo.so), whatever the configure history.
set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
include(FetchContent)
# SOURCE_SUBDIR points at nothing: MakeAvailable only downloads, so the tree can be patched before it is added.
if(KIMODO_CPP_SOURCE_DIR)
  set(_kimodo_copy "${CMAKE_BINARY_DIR}/_deps/kimodo_cpp-src")
  if(NOT EXISTS "${_kimodo_copy}/CMakeLists.txt")
    file(COPY "${KIMODO_CPP_SOURCE_DIR}/" DESTINATION "${_kimodo_copy}"
      PATTERN ".git" EXCLUDE PATTERN "models" EXCLUDE)
  endif()
  FetchContent_Declare(kimodo_cpp SOURCE_DIR "${_kimodo_copy}" SOURCE_SUBDIR "-blazma-patch-first")
else()
  FetchContent_Declare(kimodo_cpp
    GIT_REPOSITORY "${KIMODO_CPP_REPOSITORY}"
    GIT_TAG "${KIMODO_CPP_COMMIT}"
    GIT_SUBMODULES "ggml"
    GIT_SHALLOW FALSE
    SOURCE_SUBDIR "-blazma-patch-first")
endif()
# nlohmann/json (MIT), header-only: the release tarball, pinned by SHA-256 (supply chain: a moved tag
# or a replaced asset fails the configure step instead of building other code).
FetchContent_Declare(nlohmann_json
  URL "https://github.com/nlohmann/json/releases/download/v3.11.3/json.tar.xz"
  URL_HASH "SHA256=d6c65aca6b1ed68e7a182f4757257b107ae403032760ed6ef121c9d55e81757d"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
set(JSON_BuildTests OFF CACHE INTERNAL "")
FetchContent_MakeAvailable(kimodo_cpp nlohmann_json)
if(KIMODO_RUNNER_VULKAN)
  # GGML's Vulkan backend needs find_package(SPIRV-Headers CONFIG). The LunarG SDK on Windows does not
  # ship that CMake package, so the Khronos headers come from here (MIT-style licence), pinned by commit
  # (tag vulkan-sdk-1.4.309.0, the SDK version the workflow installs).
  set(SPIRV_HEADERS_SKIP_EXAMPLES ON CACHE BOOL "" FORCE)
  set(SPIRV_HEADERS_SKIP_INSTALL ON CACHE BOOL "" FORCE)
  FetchContent_Declare(SPIRV-Headers
    GIT_REPOSITORY "https://github.com/KhronosGroup/SPIRV-Headers.git"
    GIT_TAG "09913f088a1197aba4aefd300a876b2ebbaa3391"
    GIT_SHALLOW FALSE
    OVERRIDE_FIND_PACKAGE)
  FetchContent_MakeAvailable(SPIRV-Headers)
endif()
execute_process(COMMAND "${CMAKE_COMMAND}" "-DSRC=${kimodo_cpp_SOURCE_DIR}" -P "${CMAKE_CURRENT_SOURCE_DIR}/patches/patch_kimodo.cmake"
  RESULT_VARIABLE _patch_result)
if(NOT _patch_result EQUAL 0)
  message(FATAL_ERROR "patching kimodo.cpp failed")
endif()
add_subdirectory("${kimodo_cpp_SOURCE_DIR}" "${kimodo_cpp_BINARY_DIR}")
if(KIMODO_RUNNER_VULKAN AND TARGET ggml-vulkan)
  # Use the pinned Khronos layout (<spirv/unified1/spirv.hpp>) whatever the SDK provides.
  target_include_directories(ggml-vulkan PRIVATE "${spirv-headers_SOURCE_DIR}/include")
endif()

if(NOT EXISTS "${kimodo_cpp_SOURCE_DIR}/ggml/CMakeLists.txt")
  message(WARNING "kimodo.cpp was fetched without its ggml submodule: the runner will build, but generation will report that GGML is unavailable.")
endif()

add_executable(kimodo-runner src/main.cpp src/soma_skeleton.hpp)
if(WIN32)
  # UTF-8 active code page (Windows 10 1903+): non-ASCII model paths work with kimodo.cpp's narrow-char file APIs.
  # MSVC embeds .manifest sources with the linker's /MANIFESTINPUT.
  target_sources(kimodo-runner PRIVATE src/kimodo-runner.manifest)
endif()
find_package(Threads REQUIRED)
target_link_libraries(kimodo-runner PRIVATE kimodo nlohmann_json::nlohmann_json Threads::Threads)
if(TARGET ggml)
  # The runner lists GGML's devices itself (hello/diagnose), from the same registry kimodo.cpp uses.
  target_link_libraries(kimodo-runner PRIVATE ggml)
  target_compile_definitions(kimodo-runner PRIVATE KIMODO_RUNNER_HAVE_GGML=1)
endif()
if(KIMODO_RUNNER_VULKAN AND TARGET ggml-vulkan)
  target_compile_definitions(kimodo-runner PRIVATE KIMODO_RUNNER_HAVE_VULKAN=1)
endif()
if(KIMODO_RUNNER_CUDA AND TARGET ggml-cuda)
  target_compile_definitions(kimodo-runner PRIVATE KIMODO_RUNNER_HAVE_CUDA=1)
endif()
if(MSVC)
  target_compile_options(kimodo-runner PRIVATE /W4 /permissive- /utf-8)
else()
  target_compile_options(kimodo-runner PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(NOT WIN32)
  set_target_properties(kimodo-runner PROPERTIES BUILD_RPATH "\$ORIGIN" INSTALL_RPATH "\$ORIGIN")
endif()
install(TARGETS kimodo-runner RUNTIME DESTINATION .)
