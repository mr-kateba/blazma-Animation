# Third-party notices — Blazma Animation

Blazma Animation itself is proprietary: Copyright (c) 2025-2026 mr-kateba, all rights reserved (`LICENSE.txt`).
It is built on the following components. Their licences allow redistribution in binary form provided the notices
below are kept. The full licence texts are in the `legal/` folder next to this file.

## Godot Engine 4.7.2 (.NET) — MIT
Copyright (c) 2014-present Godot Engine contributors; (c) 2007-2014 Juan Linietsky, Ariel Manzur.
The full MIT text is in `legal/GODOT_LICENSE.txt`. Godot's own third-party components and their licence texts are
listed in `legal/GODOT_COPYRIGHT.txt` (the engine's COPYRIGHT.txt at tag 4.7.2-stable).

## .NET runtime and libraries — MIT
Copyright (c) .NET Foundation and Contributors. Full text: `legal/DOTNET_LICENSE.txt`.
Bundled in the app's `data_*` folder (.NET 8 runtime; notices: `legal/DOTNET8_THIRD_PARTY_NOTICES.txt`) and,
self-contained, inside `tools/blazma-mcp` and `tools/blazma-anim` (.NET 10; `legal/DOTNET10_THIRD_PARTY_NOTICES.txt`).

## ModelContextProtocol C# SDK — Apache-2.0 (older contributions MIT)
Used by `blazma-mcp`. https://github.com/modelcontextprotocol/csharp-sdk — licence file (Apache-2.0 and MIT texts):
`legal/MCP_CSHARP_SDK_LICENSE.txt`. The project ships no NOTICE file.

## IBM Plex Sans Arabic and IBM Plex Mono — SIL Open Font License 1.1
Copyright (c) 2017 IBM Corp. with Reserved Font Name "Plex". The licence text is in `legal/OFL-IBM-Plex.txt`.

## Khronos glTF sample models (only if the `samples` folder is present) — CC BY 4.0
RiggedFigure, CesiumMan and RiggedSimple from KhronosGroup/glTF-Sample-Assets.
Copyright (c) 2017 Cesium, "Cesium for Everything". Licensed under Creative Commons Attribution 4.0
International (https://creativecommons.org/licenses/by/4.0/legalcode). Unmodified copies.
CesiumMan also carries the Cesium logo texture (c) 2015 Cesium (LicenseRef-LegalMark-Cesium).

## NVIDIA Kimodo skeleton data — Apache-2.0 (always included)
The SOMA 30-joint names, parents and rest offsets in `src/Blazma.AI/SomaSkeleton.cs` (in the app and tools) and
`ai/kimodo/src/soma_skeleton.hpp` (in the runner) come from NVIDIA Kimodo (https://github.com/nv-tlabs/kimodo),
Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES, Apache-2.0 (`legal/NVIDIA_KIMODO_LICENSE.txt`, `legal/APACHE-2.0.txt`).
The project ships no NOTICE file.

## kimodo-runner (only if the `tools/kimodo` folder is present)
Blazma's local text-to-motion runner (`ai/kimodo`, built by the manual `kimodo-runner` workflow) links:
- **kimodo.cpp** — Apache-2.0 (`legal/APACHE-2.0.txt`), https://github.com/localai-org/kimodo.cpp (pinned commit in
  `ai/kimodo/CMakeLists.txt`); its NOTICE file: `legal/KIMODO_CPP_NOTICE.txt`. Modified by Blazma's build: device
  selection through GGML's backend registry (`ai/kimodo/patches/`, applied at configure time).
- **GGML** — MIT, Copyright (c) 2023-2026 The ggml authors, https://github.com/ggml-org/ggml (`legal/GGML_LICENSE.txt`).
- **SPIRV-Headers** (vulkan-sdk-1.4.309.0, compiled into `ggml-vulkan`) — MIT-style Khronos licence, Copyright (c)
  2015-2024 The Khronos Group Inc., https://github.com/KhronosGroup/SPIRV-Headers (`legal/SPIRV_HEADERS_LICENSE.txt`).
- **NVIDIA CUDA runtime** (`cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`, only with `ggml-cuda.dll`) —
  redistributed unmodified under the NVIDIA CUDA Toolkit EULA, Attachment A (`legal/NVIDIA_CUDA_NOTICE.txt`).
- **nlohmann/json 3.11.3** — MIT, Copyright (c) 2013-2022 Niels Lohmann, https://github.com/nlohmann/json (`legal/NLOHMANN_JSON_LICENSE.txt`).

No model weights are shipped. The Kimodo models the user downloads from Hugging Face inside the app carry their own
licences (NVIDIA Open Model License for the SOMA motion model; Meta Llama 3 Community License for the text encoder,
"Built with Meta Llama 3"); their licence and NOTICE files are downloaded next to the models.
