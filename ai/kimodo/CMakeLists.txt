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
# Use a local checkout instead of fetching (it must contain the ggml submodule).
set(KIMODO_CPP_SOURCE_DIR "" CACHE PATH "Local kimodo.cpp checkout (optional)")
option(KIMODO_RUNNER_VULKAN "Build GGML's Vulkan backend (needs the Vulkan SDK/headers)" ON)

set(KIMODO_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(KIMODO_ENABLE_FUZZERS OFF CACHE BOOL "" FORCE)
set(KIMODO_ENABLE_VULKAN ${KIMODO_RUNNER_VULKAN} CACHE BOOL "" FORCE)

include(FetchContent)
if(KIMODO_CPP_SOURCE_DIR)
  FetchContent_Declare(kimodo_cpp SOURCE_DIR "${KIMODO_CPP_SOURCE_DIR}")
else()
  FetchContent_Declare(kimodo_cpp
    GIT_REPOSITORY "${KIMODO_CPP_REPOSITORY}"
    GIT_TAG "${KIMODO_CPP_COMMIT}"
    GIT_SUBMODULES "ggml"
    GIT_SHALLOW FALSE)
endif()
# nlohmann/json (MIT), header-only, pinned release.
FetchContent_Declare(nlohmann_json
  GIT_REPOSITORY "https://github.com/nlohmann/json.git"
  GIT_TAG "v3.11.3"
  GIT_SHALLOW TRUE)
set(JSON_BuildTests OFF CACHE INTERNAL "")
FetchContent_MakeAvailable(kimodo_cpp nlohmann_json)

if(NOT EXISTS "${kimodo_cpp_SOURCE_DIR}/ggml/CMakeLists.txt")
  message(WARNING "kimodo.cpp was fetched without its ggml submodule: the runner will build, but generation will report that GGML is unavailable.")
endif()

add_executable(kimodo-runner src/main.cpp src/soma_skeleton.hpp)
target_link_libraries(kimodo-runner PRIVATE kimodo nlohmann_json::nlohmann_json)
if(KIMODO_RUNNER_VULKAN AND TARGET ggml-vulkan)
  target_compile_definitions(kimodo-runner PRIVATE KIMODO_RUNNER_HAVE_VULKAN=1)
endif()
if(TARGET ggml)
  target_compile_definitions(kimodo-runner PRIVATE KIMODO_RUNNER_HAVE_GGML=1)
endif()
if(MSVC)
  target_compile_options(kimodo-runner PRIVATE /W4 /permissive- /utf-8)
else()
  target_compile_options(kimodo-runner PRIVATE -Wall -Wextra -Wpedantic)
endif()

# Put the shared libraries (kimodo, ggml*) next to the executable so it runs from its own folder.
set_target_properties(kimodo-runner PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
foreach(lib kimodo ggml ggml-base ggml-cpu ggml-vulkan)
  if(TARGET ${lib})
    set_target_properties(${lib} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin" LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
  endif()
endforeach()
if(NOT WIN32)
  set_target_properties(kimodo-runner PROPERTIES BUILD_RPATH "\$ORIGIN" INSTALL_RPATH "\$ORIGIN")
endif()
install(TARGETS kimodo-runner RUNTIME DESTINATION .)
