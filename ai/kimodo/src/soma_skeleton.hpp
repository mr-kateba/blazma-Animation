// SOMA compact 30-joint control skeleton predicted by Kimodo SOMA RP/SEED v1.1.
// Names, parents and parent-local offsets are copied from kimodo.cpp src/skeleton.hpp
// (soma30_spec at commit 5679ff19), which copies them from NVIDIA Kimodo's Apache-2.0
// skeleton definitions. The hips rest offset (0, 0.988, 0) is the root translation that
// kimodo.cpp's scripts/export_glb.py writes. Rest rotations are identity.
// Axes: +Y up, +Z forward, "Left*" joints on +X (same as Blazma; no conversion).
// The C API does not return any of this, which is why the runner embeds it.
// Keep in sync with src/Blazma.AI/SomaSkeleton.cs (Blazma rejects a mismatch).
#pragma once
#include <array>
#include <string_view>

namespace blazma::soma {

inline constexpr std::string_view key = "soma30";
inline constexpr int joints = 30;
inline constexpr double fps = 30.0;

inline constexpr std::array<std::string_view, joints> names{
    "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "Jaw",
    "LeftEye", "RightEye", "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
    "LeftHandThumbEnd", "LeftHandMiddleEnd", "RightShoulder", "RightArm", "RightForeArm",
    "RightHand", "RightHandThumbEnd", "RightHandMiddleEnd", "LeftLeg", "LeftShin", "LeftFoot",
    "LeftToeBase", "RightLeg", "RightShin", "RightFoot", "RightToeBase"};

inline constexpr std::array<int, joints> parents{
    -1, 0, 1, 2, 3, 4, 5, 6, 6, 6, 3, 10, 11, 12, 13, 13, 3, 16, 17, 18, 19, 19, 0, 22, 23, 24, 0, 26, 27, 28};

inline constexpr std::array<std::array<double, 3>, joints> offsets{{
    {0, 0.988, 0}, {-.00013727, .0500376256, -.00053726669}, {-1.86574103e-9, .0712530139, -.000298248546},
    {-5.75188398e-9, .0755006305, -.00815970992}, {-.00181676517, .263112953, -.00553348292},
    {-2.85102231e-8, .0770939664, .0230258546}, {-4.5975437e-8, .0612891595, .0195370861},
    {2.63687901e-5, .0047559225, .0309494062}, {.0320638079, .0538020513, .0758688308},
    {-.0322244017, .05361869, .0755823359}, {.0162165175, .232371641, .0511341324},
    {.149198457, 2.19397873e-8, -.0550232576}, {.287393078, 2.50268389e-9, -2.58787737e-5},
    {.270939812, -7.06625108e-9, 2.60897248e-5}, {.122686267, -.0322017573, .0483306876},
    {.190119595, -.00312878387, -.000339570373}, {-.0138011824, .231803086, .0521415786},
    {-.150371962, 1.17387901e-7, -.0554560437}, {-.287366393, 1.87628082e-8, -2.59709359e-5},
    {-.271336198, -1.16767401e-9, 2.61269368e-5}, {-.122642483, -.0321145448, .0480403904},
    {-.190005945, -.00306615542, -.0003157343}, {.10043214, -.0843452671, .0259565473},
    {-1e-8, -.432217537, -.00802912805}, {1e-8, -.421550959, -.0348152298},
    {0, -.0505947206, .132315294}, {-.10047278, -.0829525995, .0262031695},
    {1e-8, -.433622059, -.00805555828}, {2e-8, -.421173943, -.0347839785},
    {-3.42907669e-9, -.0507960932, .132841956}}};

} // namespace blazma::soma
