# Third-party notices — Blazma Animation

Blazma Animation is built on the following components. Their licences allow redistribution in binary form
provided the notices below are kept.

## Godot Engine 4.7.2 (.NET) — MIT
Copyright (c) 2014-present Godot Engine contributors; (c) 2007-2014 Juan Linietsky, Ariel Manzur.
The full MIT text is in `GODOT_LICENSE.txt`. The complete list of Godot's own third-party components
(and their licences) is at https://godotengine.org/license and in the engine's `COPYRIGHT.txt`
(https://github.com/godotengine/godot/blob/4.7.2-stable/COPYRIGHT.txt).

## .NET runtime and libraries — MIT
Copyright (c) .NET Foundation and Contributors. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
Bundled in the app's `data_*` folder and, self-contained, inside `tools/blazma-mcp` and `tools/blazma-anim`.

## ModelContextProtocol C# SDK — Apache-2.0
Used by `blazma-mcp`. https://github.com/modelcontextprotocol/csharp-sdk

## IBM Plex Sans Arabic and IBM Plex Mono — SIL Open Font License 1.1
Copyright (c) 2017 IBM Corp. with Reserved Font Name "Plex". The licence text is in `OFL-IBM-Plex.txt`.

## Khronos glTF sample models (only if the `samples` folder is present) — CC BY 4.0
RiggedFigure, CesiumMan and RiggedSimple from KhronosGroup/glTF-Sample-Assets.
Copyright (c) 2017 Cesium, "Cesium for Everything". Licensed under Creative Commons Attribution 4.0
International (https://creativecommons.org/licenses/by/4.0/legalcode). Unmodified copies.
CesiumMan also carries the Cesium logo texture (c) 2015 Cesium (LicenseRef-LegalMark-Cesium).

## kimodo-runner (only if the `tools/kimodo` folder is present)
Blazma's local text-to-motion runner (`ai/kimodo`, built by the manual `kimodo-runner` workflow) links:
- **kimodo.cpp** — Apache-2.0, https://github.com/localai-org/kimodo.cpp (pinned commit in `ai/kimodo/CMakeLists.txt`).
- **GGML** — MIT, Copyright (c) 2023-2024 The ggml authors, https://github.com/ggml-org/ggml.
- **nlohmann/json 3.11.3** — MIT, Copyright (c) 2013-2022 Niels Lohmann, https://github.com/nlohmann/json.

No model weights are shipped. The Kimodo models the user downloads from Hugging Face inside the app carry their own
licences (NVIDIA Open Model License for the SOMA motion model; Meta Llama 3 Community License for the text encoder,
"Built with Meta Llama 3"); their licence and NOTICE files are downloaded next to the models.
