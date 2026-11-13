# Changelog

All notable changes to Blazma Animation. Versions follow the release tags (`v<version>`); notes for 0.12.0 and earlier are on the
[releases page](https://github.com/mr-kateba/blazma-Animation/releases) and in `docs/STATUS.md`.

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
