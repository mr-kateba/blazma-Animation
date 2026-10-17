# تكامل الذكاء الاصطناعي

نفرّق بين نوعين:

| النوع | الدور | التقنية |
|---|---|---|
| **Agent AI** | يخطط ويتحكم في البرنامج | Claude Code / Codex عبر MCP أو CLI |
| **Motion AI** | يولّد بيانات الحركة | kimodo.cpp (اختياري) أو أي Provider آخر |

الوكيل يستطيع استدعاء Motion AI عبر الأمر `motion.generate`، أي أنه يمر بطبقة الأوامر نفسها.

## 1. CLI — `blazma-anim`

```
blazma-anim project create --name "Demo" --out demo.blazanim
blazma-anim rig inspect character.glb
blazma-anim animation validate walk.blazanim --clip Walk
blazma-anim animation export walk.blazanim --clip Walk --format glb --out walk.glb
blazma-anim exec demo.blazanim --command keyframe.add --args '{...}'
```

- الإخراج الافتراضي نص مقروء، و`--json` يعطي `CommandResult` كاملًا.
- رموز الخروج: `0` نجاح، `1` خطأ مستخدم/مدخلات، `2` فشل تحقق، `3` خطأ داخلي، `130` إلغاء (Ctrl+C).
- التقدّم يُرسل كسطور JSON على stderr عند `--progress json`.
- كل أمر فرعي يُولَّد من سجل الأوامر، فـ`--help` والوثائق متطابقان دائمًا.

## 2. خادم MCP — `blazma-mcp`

- نقل stdio (الأكثر توافقًا مع Claude Code وCodex). يعمل في وضعين: متصل بالتطبيق المفتوح عبر IPC، أو Headless.
- **لا تُعلن أي أداة قبل تنفيذها واختبارها.** القائمة المقترحة في المواصفات تُفعَّل تدريجيًا حسب المراحل (انظر FEATURE_MATRIX.md).
- أسماء أدوات MCP مشتقة من معرّف الأمر: `keyframe.add` ← `keyframe_add`.
- لا توجد أداة لتشغيل أوامر النظام، ولا أداة لقراءة أو كتابة ملفات خارج مساحة المشروع والمجلدات المسموح بها صراحةً.

## 3. الصلاحيات

| التأثير | Assisted Mode | Autonomous Mode |
|---|---|---|
| ReadOnly | ينفَّذ مباشرة | ينفَّذ مباشرة |
| Mutating | يُنشأ **مسودة** مع Preview وينتظر Approve | ينفَّذ، ويُنشأ Restore Point قبل أول تعديل في المهمة |
| Destructive (حذف، الكتابة فوق ملف) | يحتاج موافقة دائمًا | يحتاج موافقة دائمًا |
| External (تصدير إلى مسار خارج المشروع) | موافقة، أو مسار ضمن قائمة مسموحة | مسار ضمن قائمة مسموحة فقط |

- **المسودات ومجموعة التغييرات (Changeset):** في Assisted Mode لا يُطلب اعتماد لكل أمر منفرد، فهذا غير عملي للوكلاء. كل تعديلات المهمة الواحدة تُجمَّع في Changeset واحد يُكتب في مقطع مسودة (`clip.draft = true`)، ثم تُعرض معاينة واحدة ويُعتمد مرة واحدة بأمر `draft.apply` قابل للتراجع كخطوة واحدة.
- **نقطة الاستعادة (Restore Point):** لقطة كاملة لمجلد الجلسة تؤخذ قبل أول تعديل في المهمة، وتُحذف بعد اعتماد المهمة أو رفضها، مع حد أقصى لعدد النقاط المحفوظة.
- **الإلغاء:** كل مهمة لها `CancellationToken`. الإلغاء من لوحة AI Workspace أو من العميل يوقف التنفيذ ويعيد الحالة إلى Restore Point إن طُلب ذلك.

## 4. واجهة مزوّد الحركة (منفّذة في `src/Blazma.AI`)

```csharp
public interface IMotionGenerationProvider {
    string Id { get; }                                   // "kimodo" | "procedural"
    ProviderInfo Describe();                              // رخيص: فحص ملفات فقط، لا يشغّل أي عملية
    Task<ProviderDiagnostics> DiagnoseAsync(bool probe, CancellationToken ct); // العتاد + الملفات (+ سؤال الـrunner عند probe)
    Task<MotionResult> GenerateAsync(MotionRequest req,  // prompt, durationSeconds, fps, seed, constraints?
        IProgress<GenerationProgress>? progress, CancellationToken ct);
}
```

- `MotionResult`: تعريف الهيكل المصدر (`SkeletonDefinition`: الأسماء، الآباء، وضعية الراحة المحلية، المحور العلوي `+Y` والأمامي `+Z`، وخريطة إلى أسماء Blazma القياسية)، ثم لكل إطار دوران محلي لكل مفصل (نسبةً إلى الأب، الراحة = دوران الهوية في SOMA) وموضع الجذر، وFPS، وعدد المفاصل، والتحذيرات.
- المزوّدات:
  - **`kimodo`**: يشغّل عملية `kimodo-runner` الخارجية (انظر §5). اختياري، ولا يعمل حتى يثبّته المستخدم بنفسه.
  - **`procedural`**: مولّد داخلي حتمي **ليس ذكاءً اصطناعيًا**، للاختبارات والعرض دون إنترنت. يفهم كلمات مفتاحية فقط: walk, run, jump, wave, squat, idle (وبالعربية: مشي، ركض، قفز، تلويح، قرفصاء، وقوف) ويولّد حركة على هيكل SOMA نفسه، فيمر بمسار التحويل نفسه. المشي فيه تتبادل فيه القدمان وقدم الارتكاز ثابتة على الأرض والجذر يتقدم نحو ‎+Z. أي نص آخر يعيد الخطأ `unsupported_prompt`.
- **التحويل وRetarget** (`MotionRetargeter`): لكل عظمة مطابَقة: `G_b(t) = Δ_s(t) · C_b · Rest_b` حيث `Δ_s(t) = G_s(t) · Rest_s⁻¹` (دورانات عالمية) و`C_b` أقصر دوران ينقل اتجاه العظمة في وضعية راحة الهدف إلى اتجاهها في المصدر. النتيجة: كل عظمة تشير في العالم إلى الاتجاه نفسه كما في المصدر، مهما اختلفت وضعيات الراحة (T/A-pose) ومحاور العظام. إزاحة الجذر تُضرب في نسبة طول الساق (أو ارتفاع الورك). إعادة العيّنة إلى FPS المقطع، وتثبيت القدمين اختياريًا عند التلامس (منخفضة وبطيئة) بـTwo-bone IK، ثم اختزال المفاتيح الخطية بسماحية (0.1° / 0.5 مم). العظام غير المطابقة (الأصابع، الوجه، عظام إضافية) تبقى في وضعية الراحة مع تحذير (R5). الناتج مقطع مسودة `IsDraft = true` وفيه `Metadata`: provider, prompt, seed, durationSeconds, character, source.*.

### الأوامر والمهام الطويلة (DECISIONS D24)

طبقة الأوامر متزامنة و`ProjectSession` أحادي الخيط، لذلك:

1. `motion.generate {characterId, prompt, durationSeconds, fps?, seed?, provider?, name?, footCleanup?, reduceKeys?, loop?, inPlace?}` **لا يغيّر المشروع** (ReadOnly مثل D19): يتحقق من المدخلات، ويأخذ نسخة خاصة من الـRig، ويبدأ Job في الخلفية، ويعيد `jobId`. بدون `provider` يُستخدم kimodo إن كان مثبّتًا، وإلا فالخطأ `provider_unavailable` مع رسالة تشرح التثبيت وتقترح `provider: "procedural"`.
2. الـJob يعمل على خيط عامل: المزوّد ← `MotionResult` ← Retarget ← مقطع مسودة جاهز في الذاكرة. لا يلمس الجلسة أبدًا.
3. `motion.job_status {jobId?, waitSeconds?}` يعيد الحالة (`running | ready | failed | cancelled | applied`) والتقدّم والمرحلة والتحذيرات. `waitSeconds` ينتظر فقط في الوضع Headless (CLI/MCP بدون التطبيق)؛ داخل التطبيق يُتجاهل مع تحذير `wait_ignored` حتى لا يتجمّد الخيط الرئيسي.
4. `motion.job_cancel {jobId}` يلغي (Kimodo: تُنهى عملية الـrunner).
5. `motion.job_apply {jobId, name?}` (Mutating، قابل للتراجع) يضيف المقطع كمسودة على خيط الجلسة ويعيد `clipId`. التطبيق ينفّذه على الخيط الرئيسي (عند حدث الجاهزية أو بضغطة المستخدم)، والوكيل يستدعيه كأداة.
6. `clip.accept_draft {clipId}` (قابل للتراجع) يحوّل المسودة إلى مقطع عادي، والرفض بـ`clip.delete`.
7. `motion.providers {probe?}` (ReadOnly): التوفر، وهل وُجد الـrunner والملفات، وCPU/RAM/GPU قدر الإمكان ودون شبكة. `probe` يسأل الـrunner نفسه (Headless فقط).

سير عمل الوكيل: `motion_generate` ← `motion_job_status` (مع `waitSeconds` في Headless) ← `motion_job_apply` ← تعديل المفاتيح ← `clip_accept_draft`. مثال سكربت CLI في `tests/Blazma.Tests/AI/AICommandTests.cs` (`Cli_Script_GeneratesAppliesAndAccepts`).

## 5. kimodo.cpp — ما تم التحقق منه

المصدر: <https://github.com/localai-org/kimodo.cpp>، **تمت قراءة الكود في 2026-10-17 عند الـcommit `5679ff19ba0a522c0b0516e9a9d402fe1af2c027`** (بتاريخ 2026-09-17). الملفات المقروءة: `README.md`، `include/kimodo/kimodo_capi.h`، `src/capi.cpp`، `src/skeleton.hpp`، `src/motion_decode.cpp`، `src/motion_rep.cpp`، `src/generate.cpp`، `src/ggml_weights.cpp`، `src/llm_text_encoder.cpp`، `scripts/export_glb.py`، `scripts/download_gguf_weights.py`، `demo/main.go`، `CMakeLists.txt`، `CMakePresets.json`، `.gitmodules`.

**الكود والترخيص**
- منفذ C++/GGML لنموذج NVIDIA Kimodo. **ترخيص الكود Apache-2.0**، وGGML والأوزان لها تراخيص منفصلة (README، قسم License).
- خمس نقاط حفظ: SMPL-X RP v1 ‏(22 مفصلًا)، **SOMA RP/SEED v1.1 ‏(30 مفصلًا، هيكل التحكم المضغوط)**، G1 RP/SEED v1 ‏(34 مفصلًا، روبوت Unitree).
- جدول التراخيص في README: SOMA وG1 تحت NVIDIA Open Model License و"Commercial use: Permitted by the model licence". SMPL-X تحت NVIDIA Internal Scientific Research and Development Model License: لا استخدام تجاري ولا إعادة توزيع للنماذج المشتقة، وهو **غير موجود عمدًا** في سكربت تنزيل الأوزان. حزمة مُرمِّز النص تتضمن مواد Meta Llama 3 بشروطها الخاصة. **ما زالت مراجعة النص الكامل للتراخيص مطلوبة قبل أي توزيع (R3).**

**واجهة C ‏(`kimodo_capi.h`، `KIMODO_CAPI_ABI_VERSION 1`)**
- `kimodo_abi_version`، `kimodo_model_load(motion_gguf, text_gguf, text_adapter_gguf, options, err, len)`، `kimodo_model_free`، `kimodo_model_last_error`، `kimodo_generate(model, prompt, options, err, len)` (نص UTF-8)، `kimodo_generate_embedding` (متجه 4096 قيمة F32)، `kimodo_motion_free/frames/joints`، `kimodo_motion_local_rotations_xyzw` ‏(`[T,J,4]`) و`kimodo_motion_root_positions` ‏(`[T,3]`).
- `kimodo_generation_options`: ‏`seed`، `frames` (من 1 إلى 10000)، `diffusion_steps` (من 1 إلى 1000)، `text_cfg_weight`، `constraint_cfg_weight`. أداة `kmd-generate` تستخدم 2.0 و2.0، والعرض التجريبي يستخدم 100 خطوة افتراضيًا.
- **`text_adapter_gguf` يجب أن يكون NULL** ("separate text adapters are unsupported"). معامل النص هو ملف GGUF واحد بجانبه `tokenizer.gguf`، أو مجلد المكونات القديم.
- **`kimodo_runtime_options` (الخيوط والجهاز) يُتحقق من حجمه فقط ولا يُستخدم** في هذه النسخة. الجهاز يُختار من متغير البيئة `KIMODO_BACKEND` (`cpu` يفرض المعالج، وإلا Vulkan إن وُجد ثم CPU)، والخيوط من `KIMODO_THREADS`. لذلك يضبطهما `kimodo-runner` قبل التحميل.
- **لا يوجد callback للتقدّم ولا إلغاء**: `kimodo_generate` استدعاء حاجب. لذلك يرسل الـrunner مراحل فقط (تحميل، توليد، إرسال)، والإلغاء = إنهاء العملية.
- الواجهة **لا تُرجع** أسماء المفاصل ولا الآباء ولا وضعية الراحة. وREADME يقول "query the joint count from the result rather than assuming a fixed skeleton".

**صيغة المخرجات** (من `src/motion_decode.cpp` و`scripts/export_glb.py`)
- الدوران المحلي = `transpose(G_parent) · G_joint` محوّلًا من تمثيل 6D العالمي، أي دوران نسبةً إلى الأب، **ووضعية الراحة دورانها هوية** (ملف GLB الذي يصدّره المشروع يكتب للعقد إزاحات فقط).
- `root_positions` هو الموضع المطلق لمفصل `Hips` (يُكتب مباشرة كـtranslation لعقدة Hips في GLB، وارتفاعه في الراحة 0.988 م).
- المعدل **30 إطارًا في الثانية** (`fps=30` افتراضيًا في `export_glb.py` و`motion_rep.hpp`).
- هيكل SOMA ‏(30): `Hips, Spine1, Spine2, Chest, Neck1, Neck2, Head, Jaw, LeftEye, RightEye, LeftShoulder, LeftArm, LeftForeArm, LeftHand, LeftHandThumbEnd, LeftHandMiddleEnd, Right… (مثلها), LeftLeg, LeftShin, LeftFoot, LeftToeBase, Right… (مثلها)` مع الآباء والإزاحات في `src/skeleton.hpp` (منقولة عن تعريفات NVIDIA بترخيص Apache-2.0). المحاور: ‎+Y أعلى، القدمان نحو ‎+Z، والجانب الأيسر على ‎+X، **مطابقة لمحاور Blazma** فلا تحويل. هذه البيانات مضمّنة في `ai/kimodo/src/soma_skeleton.hpp` و`src/Blazma.AI/SomaSkeleton.cs`، وBlazma يرفض أي هيكل مختلف يرسله الـrunner.
- غير منفّذ في kimodo.cpp: إدخال القيود (constraints)، والتوسيع إلى 77 مفصلًا، وتصدير GLB بشبكة مكسوّة. لذلك لا أصابع ولا وجه (R5)، وتُتجاهل القيود مع تحذير.

**البناء**: C++23، CMake 3.25+، Ninja. GGML وحدة فرعية مثبّتة (`ggml` عند `8c63e709`، الإصدار 0.20.2). Vulkan اختياري (`KIMODO_ENABLE_VULKAN`، افتراضيًا ON إن وُجدت الترويسات) مع CPU كاحتياط. لم يُذكر CUDA.

**الأوزان** (لا تُنزَّل تلقائيًا أبدًا): منشورة على Hugging Face تحت منظمة `LocalAI-io`. أسماء الملفات: `kimodo-soma-rp-v1.1-f32.gguf`، `kimodo-soma-seed-v1.1-f32.gguf` (ومثلها لـG1)، ومُرمِّز النص `Llama-3-Kimodo-Q8_0.gguf` (الافتراضي) أو `Q6_K` / `Q5_K` / `Q4_K` / `Q4_K_M` / `BF16`، وكلها تشترك في `tokenizer.gguf`.

**الذاكرة**: العرض التجريبي يُبقي مُرمِّز Q8 كاملًا في VRAM بسقف 10 GiB على بطاقة 16 GiB. المكتبة افتراضيًا تنفّذ 8 طبقات في كل دفعة (`KIMODO_TEXT_LAYER_CHUNK` من 1 إلى 32) ويمكن فرض سقف بـ`KIMODO_TEXT_RESIDENT_LIMIT_MIB`. **لم نقِس الأداء ولا الذاكرة بعد، ولا يُعتمد أي رقم قبل القياس (R4).**

**ما تحققنا منه عمليًا في هذه المرحلة**
- بُني `kimodo-runner` على Linux ‏(GCC 13.3، CMake 3.28، Ninja، CPU فقط دون Vulkan) فوق kimodo.cpp المثبّت بالـcommit أعلاه، وجُرّب البروتوكول على الملف التنفيذي الحقيقي: `hello`، `diagnose`، خطأ ملف غير موجود (`models_not_found`)، ملف GGUF وهمي (`model_load_failed: truncated GGUF header`)، سطر غير صالح، `shutdown`. وجُرّب أيضًا من `KimodoProvider` في C#: التشخيص مع probe، والتقدّم، والخطأ النظيف.
- **غير متحقق منه**: توليد حقيقي (لا أوزان في البيئة ولم يُشغَّل أي نموذج)، البناء على Windows/MSVC وبناء Vulkan (يوجد workflow يدوي `.github/workflows/kimodo-runner.yml` لم يُشغَّل بعد)، الأداء والذاكرة، وصحة الاتجاهات على مخرجات حقيقية (اختُبرت على بيانات مصطنعة بالصيغة نفسها).

### بروتوكول `kimodo-runner` ‏(الإصدار 1)

- العملية: `kimodo-runner --stdio`. سطر JSON-RPC 2.0 واحد لكل رسالة (UTF-8، ينتهي بـ`\n`). stdout للبروتوكول فقط، وstderr للسجلات (يلتقط Blazma آخر 40 سطرًا ويقطع السطر عند 2000 حرف).
- حدود الحجم: الطلب ≤ 1 MiB (في الـrunner)، والرد ≤ 32 MiB افتراضيًا (في Blazma). تجاوز الحد أو سطر غير صالح ← تُنهى العملية ويُعاد `message_too_large` أو `protocol_error`.

| الطريقة | الاتجاه | المعاملات | النتيجة |
|---|---|---|---|
| `hello` | طلب | `{protocol: 1, client}` | `{protocol, runner, version, kimodoAbi, skeletons, ggml, vulkanCompiled}` |
| `diagnose` | طلب | `{motionModel?, textModel?, device?}` | `{ggml, vulkanCompiled, kimodoAbi, motionModelExists, textModelExists, modelLoaded, warnings}` |
| `generate` | طلب | `{prompt, frames, seed, steps, motionModel, textModel, device: auto\|cpu\|vulkan, threads}` | `{fps: 30, frames, joints: 30, skeleton: {key: "soma30", up: "+Y", forward: "+Z", names, parents, offsets}, rotations: [x,y,z,w,…] (T·J·4), rootPositions: [x,y,z,…] (T·3), model, textModel, steps, seed, runner, warnings}` |
| `progress` | إشعار من الـrunner | `{id, stage: load_model\|generate\|decode, fraction, message?}` | — |
| `cancel` | إشعار إلى الـrunner | `{id}` | لا شيء؛ Blazma ينهي العملية بعده مباشرة |
| `shutdown` | إشعار إلى الـrunner | `{}` | تخرج العملية (يُرسل عند الخمول) |

الأخطاء: `{"error": {"code": -32000, "message": "...", "data": {"code": "models_not_found" | "model_load_failed" | "generation_failed" | "device_unavailable" | "unsupported_skeleton" | "invalid_request" | "unknown_method"}}}`. يحتفظ الـrunner بالنموذج محمّلًا بين الطلبات (حسب المسارات والجهاز).

دورة الحياة في Blazma (`KimodoProvider`): لا تبدأ العملية إلا عند أول طلب، وتُوقف بعد 3 دقائق خمول (`shutdown` ثم إنهاء). توليد واحد في كل مرة. مهلات: البدء 20 ث، التشخيص 30 ث، التوليد 15 دقيقة (قابلة للتغيير). الانهيار يعطي `runner_crashed` مع رمز الخروج وآخر سطور السجل، والطلب التالي يبدأ عملية جديدة. الإلغاء والمهلة ينهيان العملية.

### الإعدادات

| الإعداد | متغير البيئة | الافتراضي |
|---|---|---|
| مسار `kimodo-runner` | `BLAZMA_KIMODO_RUNNER` | — (غير مثبّت) |
| مجلد النماذج (ويُبحث أيضًا في مجلده الفرعي `models`) | `BLAZMA_KIMODO_MODELS` | — |
| النموذج | `BLAZMA_KIMODO_MODEL` | `soma-rp-v1.1` (أو `soma-seed-v1.1`؛ G1 وSMPL-X غير مدعومين) |
| مُرمِّز النص (اسم ملف أو مسار) | `BLAZMA_KIMODO_TEXT_MODEL` | أول الموجود: Q8_0، Q6_K، Q5_K، Q4_K_M، Q4_K، BF16 |
| الجهاز | `BLAZMA_KIMODO_DEVICE` | `auto` (`cpu` يفرض المعالج، `vulkan` يفشل إن بُني الـrunner بدونه) |
| مهلة التوليد بالثواني | `BLAZMA_KIMODO_TIMEOUT_SECONDS` | 900 |

التطبيق يمرّر إعداداته عبر `MotionJobService.Kimodo.Settings = new KimodoSettings {…}`. **الوكلاء لا يستطيعون تغيير هذه الإعدادات** لأنها تحدد ملفًا تنفيذيًا (D28).

### تثبيت Kimodo يدويًا (المستخدم)

1. **بناء الـrunner**: من جذر المستودع: `cmake -S ai/kimodo -B ai/kimodo/build -G Ninja -DCMAKE_BUILD_TYPE=Release` (أضف `-DKIMODO_RUNNER_VULKAN=OFF` إذا لم يكن Vulkan SDK مثبّتًا) ثم `cmake --build ai/kimodo/build --target kimodo-runner`. الناتج في `ai/kimodo/build/bin/` مع مكتبات GGML بجانبه. على Windows: Visual Studio 2022 (MSVC) أو workflow ‏`kimodo-runner (optional)` اليدوي في GitHub Actions. يحتاج البناء إلى الإنترنت لجلب kimodo.cpp وGGML وnlohmann/json بالإصدارات المثبّتة.
2. **تنزيل الأوزان بنفسك** (Blazma لا ينزّل شيئًا): اقرأ بطاقات النماذج وتراخيصها أولًا، ثم نزّل من Hugging Face ‏`LocalAI-io/Kimodo-SOMA-RP-v1.1-GGML` الملف `models/kimodo-soma-rp-v1.1-f32.gguf`، ومن `LocalAI-io/Llama-3-Kimodo-GGML` الملفين `Llama-3-Kimodo-Q8_0.gguf` و`tokenizer.gguf` (أو استخدم `scripts/download_gguf_weights.sh` من kimodo.cpp). ضعها في مجلد واحد (أو في مجلد فرعي `models`).
3. اضبط `BLAZMA_KIMODO_RUNNER` و`BLAZMA_KIMODO_MODELS` (أو من إعدادات التطبيق عندما تتوفر واجهتها)، ثم تحقق بـ`blazma-anim exec <file> motion.providers --arg probe=true`.

الأوزان والمجلدات `build` لا تُرفع إلى Git أبدًا (`.gitignore` يتجاهل `*.gguf` و`ai/kimodo/build*/`).

### خطة الدمج (الحالة)
1. ✅ `kimodo-runner` (C++ يغلّف واجهة C) كعملية منفصلة بـJSON-RPC عبر stdio — مبني على Linux، وWindows عبر workflow غير مُجرَّب.
2. ✅ المستخدم يحدد المسارات يدويًا. **لا تنزيل تلقائي.**
3. ✅ SOMA ‏(30 مفصلًا) ← هيكل Blazma القياسي عبر `SomaSkeleton.ToStandard` المختبر على الـMannequin وعلى RiggedFigure وCesiumMan.
4. ✅ الناتج مفاتيح خطية قابلة للتحرير مع اختزال بسماحية، ويُحفظ Prompt وSeed والإعدادات في `Metadata`.
5. ✅ اختبار القبول (بالمزوّد الإجرائي): توليد ← مسودة ← قبول ← تعديل مفتاح ← تصدير GLB ← تحقق (`Acceptance_TextToMotion_Keys_Edit_Export`). يبقى تكراره بأوزان Kimodo حقيقية على جهاز المستخدم.

## 6. الوثائق الموجّهة للوكلاء (تُكتب في المرحلة 5)
`AI_GUIDE.md`، `MCP_TOOLS.md` (مولّدة تلقائيًا)، `CLI_REFERENCE.md` (مولّدة تلقائيًا)، `ANIMATION_SCHEMA.md`، `AGENT_RECIPES.md`.
