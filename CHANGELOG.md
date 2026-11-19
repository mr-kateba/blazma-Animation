# Changelog

All notable changes to Blazma Animation. Versions follow the release tags (`v<version>`); notes for 0.12.0 and earlier are on the
[releases page](https://github.com/mr-kateba/blazma-Animation/releases) and in `docs/STATUS.md`.

## 1.0.1 — 2026-11-19

Fixes for what testers hit with 1.0.0 on Windows.

### AI motion generation
- **No Visual C++ setup any more (D49):** the Microsoft Visual C++ runtime that kimodo-runner and its GGML libraries need now ships
  next to them in `tools\kimodo` (Microsoft's app-local deployment), in the installer and in the portable zip. The installer no longer
  asks for administrator rights for `vc_redist.x64.exe` and no longer shows the "Visual C++ runtime was not installed" warning. With an
  old or missing system runtime the graphics card backend could not load in 1.0.0, so Kimodo reported "no graphics card".
- **Automatic never stops at "no graphics card":** when kimodo-runner finds no usable CUDA or Vulkan device, Automatic (and
  "Graphics card + processor") generates on the processor and says so: "No usable graphics card was found, so the motion was
  generated on the processor", with the reason. The order stays CUDA → Vulkan → graphics card + processor → processor. Only
  "Graphics card only" still fails, with the reason.
- **Why the graphics card was not used:** Blazma reads the GGML backend libraries' import tables (nothing is loaded) and names the
  cause: a missing file (Windows error 126), a too-old library (Windows error 127), no Vulkan driver (`vulkan-1.dll`), no NVIDIA driver
  (`nvcuda.dll`), or "the files are fine but the driver offers no Vulkan device". Shown after generation, in **Check** and in the log.
- **GeForce RTX 50 (Blackwell):** the optional CUDA download is built with CUDA 12.4, which cannot target compute capability 12.0. It is
  no longer offered or used on such a card (detected with `nvidia-smi --query-gpu=compute_cap`, or from the card name); Kimodo runs
  on it through Vulkan and Settings → AI says why. `cuda-backend.json` can now list the compiled architectures, and the
  kimodo-runner workflow builds the CUDA backend with CUDA 12.8 including sm_120 for a later release.
- **Errors in Arabic:** generation errors (no graphics card, out of memory, missing model files, runner start failures, a missing
  runtime library, CUDA driver errors, protocol mismatch) are translated, and the step text and any English part are wrapped in
  Unicode isolates so mixed Arabic/English lines read in the right order. A runner that exits with 0xC0000135 / 0xC0000139 (missing or
  too-old library) gets an actionable message.

### Motion planner
- Causative words such as «خليه»، «خلّيه»، «خليها»، «خله»، «خلها»، «خلّ»، «خلوه»، «اجعله»، «خلي الشخصية»، «خلي الرجال» are read as
  "make him / her …": the step shows «يجري», not «خليه يجري», and they never trigger a "did you mean" hint.
- «يتشقلب»، «تشقلب»، «شقلبة» (and the common misspelling «يتشقبل») are a somersault: "a person does a forward flip"; «يتشقلب للخلف»
  is a backflip. «يتدحرج» stays rolling on the ground.
- "Did you mean" hints are stricter: short words one unrelated letter away from a dictionary word (such as «خليه» → «خلسه») get none.
  The note for an automatic correction reads naturally in Arabic.

### Releases
- Release notes come from this file's section for the version (plus the install block) instead of the old phase reports.
- The MCP server reports version 1.0.1.

## 1.0.0 — 2026-11-13

Everything since v0.12.0.

### Motion
- **Physics polish (D46):** after generation and after applying a template, motion is cleaned up by default: feet stay planted
  without sliding, nothing sinks into the ground, jumps follow a ballistic arc, the joins between planned steps keep their momentum,
  falls accelerate and settle, with light secondary motion and balance (`motion.physics_polish`; `physicsPolish=false` turns it off).
  The procedural jump, fall and run were rebuilt on the same physical rules.
- **Deeper Arabic understanding in the motion planner:** text normalisation, stemming, typo repair, a lexicon that covers several
  dialects, negation ("without running"), counts ("jump three times"), layered actions, a confidence score and suggestions when a
  step is not understood.

### First launch and learning
- **Welcome screen** on the very first launch: the language is pre-selected from the operating system (Arabic regions get Arabic),
  plus the theme choice.
- **Interactive tutorial** ("How Blazma works") that lights up the real buttons step by step, with an optional "try it" step;
  reopen it from the **?** button or Help > Tutorial (Shift+F1).
- **Keyboard shortcuts** in one central, customisable map: a searchable settings page with conflict warnings, import and export,
  and a printable cheat sheet. The Generate dialog shows planner hints while you type.

### AI and installer
- **Optional CUDA download (D47):** the installer and the zip no longer carry the CUDA libraries (about 150 MB instead of about
  460 MB). On a PC with an NVIDIA card, Settings > AI offers "Download GPU acceleration (CUDA)": downloaded only when you press the
  button, with progress, cancel and resume, SHA-256 checks of the archive and of every file, and removal. Generation picks it up
  automatically.

### Fixes and polish for 1.0
- **The window fits laptop screens:** at 1280×720, 1366×768, 1440×900 and 1536×864, in Arabic and English, with Advanced mode,
  every side panel and the AI dock open, nothing is cut off any more. The top bar makes room step by step (app name, then the mode,
  language and theme switches move into a **⋯** menu, then the panel buttons show icons), the side panels scroll, the timeline
  buttons wrap onto a second line, the tool rail drops its labels on a short stage, and the project outline steps aside when there
  is still not enough width. A UI self-test checks every size in both languages.
- **Markers no longer jump when dragged:** dragging a marker by its label moved it to the frame under the cursor instead of keeping
  the point where it was grabbed (visible as a one-frame jump at some window widths).
- **One convention for sizes:** downloads, the models list, download progress, the CUDA package, free space and exported files are
  shown in decimal units (1 GB = 1,000,000,000 bytes), the way Hugging Face and download pages state them; the Light package now
  reads 6.2 GB everywhere (it showed 5.8 "GB"). RAM and graphics memory keep the Windows convention ("16 GB RAM"). Units are
  translated in Arabic (جيجابايت، ميجابايت).
- **Arabic is never set in the monospace font:** drop-downs, values and hints that used the code font switch to the Arabic UI font
  in Arabic (for example «Kimodo (ذكاء اصطناعي محلي)»), and Arabic letters inside code-style text use it as a fallback.
- Arabic translations for the new strings, the onion-skin range menu follows a language change, file-type names in file dialogs are
  translated.
- The MCP server reports version 1.0.0.
