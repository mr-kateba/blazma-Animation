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
- **التحويل وRetarget** (`MotionRetargeter`): لكل عظمة مطابَقة: `G_b(t) = Δ_s(t) · C_b · Rest_b` حيث `Δ_s(t) = G_s(t) · Rest_s⁻¹` (دورانات عالمية) و`C_b` أقصر دوران ينقل اتجاه العظمة في وضعية راحة الهدف إلى اتجاهها في المصدر. النتيجة: كل عظمة تشير في العالم إلى الاتجاه نفسه كما في المصدر، مهما اختلفت وضعيات الراحة (T/A-pose) ومحاور العظام. إزاحة الجذر تُضرب في نسبة طول الساق (أو ارتفاع الورك). إذا كان في الـRig عظمة مطابَقة لـ`root` فوق الحوض (الـMannequin ومعظم هياكل الألعاب) يُنقل **الانتقال الأفقي** إليها: تتبع إسقاط الحوض على الأرض بمتوسط متماثل ±0.25 ث، ويحتفظ الحوض بارتفاعه وتمايله وإزاحته النسبية فوقها، وتبقى تحويلات كل العظام تحت الحوض في فضاء النموذج كما هي (القدمان المثبّتتان لا تتغيران). لذلك لا يبقى الـroot في نقطة الأصل، وتتبع أذرع الـPole الشخصية، ويعمل التصدير بـ`rootMotion=in_place` (D30). إعادة العيّنة إلى FPS المقطع، وتثبيت القدمين اختياريًا عند التلامس (منخفضة وبطيئة) بـTwo-bone IK، ثم اختزال المفاتيح الخطية بسماحية (0.1° / 0.5 مم). العظام غير المطابقة (الأصابع، الوجه، عظام إضافية) تبقى في وضعية الراحة مع تحذير (R5). الناتج مقطع مسودة `IsDraft = true` وفيه `Metadata`: provider, prompt, seed, durationSeconds, character, source.*.

### الأوامر والمهام الطويلة (DECISIONS D24)

طبقة الأوامر متزامنة و`ProjectSession` أحادي الخيط، لذلك:

1. `motion.generate {characterId, prompt, durationSeconds, fps?, seed?, provider?, name?, footCleanup?, reduceKeys?, loop?, inPlace?}` **لا يغيّر المشروع** (ReadOnly مثل D19): يتحقق من المدخلات، ويأخذ نسخة خاصة من الـRig، ويبدأ Job في الخلفية، ويعيد `jobId`. بدون `provider` يُستخدم kimodo إن كان مثبّتًا، وإلا فالخطأ `provider_unavailable` مع رسالة تشرح التثبيت وتقترح `provider: "procedural"`.
2. الـJob يعمل على خيط عامل: المزوّد ← `MotionResult` ← Retarget ← مقطع مسودة جاهز في الذاكرة. لا يلمس الجلسة أبدًا.
3. `motion.job_status {jobId?, waitSeconds?}` يعيد الحالة (`running | ready | failed | cancelled | applied`) والتقدّم والمرحلة والتحذيرات والمالك (`owner`). `waitSeconds` ينتظر فقط في الوضع Headless (CLI/MCP بدون التطبيق) **وبحد أقصى 25 ثانية لكل استدعاء** (تحذير `wait_capped`؛ استدعِ مرة أخرى لمواصلة الانتظار)، لأن الخادم Headless يجيب طلبًا واحدًا في كل مرة. إغلاق الخدمة يُنهي أي انتظار. داخل التطبيق يُتجاهل مع تحذير `wait_ignored` حتى لا يتجمّد الخيط الرئيسي.
4. `motion.job_cancel {jobId}` يلغي (Kimodo: تُنهى عملية الـrunner). الإلغاء الذي يصل أثناء الـRetarget يُنهي المهمة بحالة `cancelled` لا `ready`.
5. `motion.job_apply {jobId, name?}` (Mutating، قابل للتراجع) يضيف المقطع كمسودة على خيط الجلسة ويعيد `clipId`. التطبيق ينفّذه على الخيط الرئيسي (عند حدث الجاهزية أو بضغطة المستخدم)، والوكيل يستدعيه كأداة. قبل الإضافة يتحقق من أن المهمة **لم تتقادم**: نفس المشروع (`Project.Id`)، والشخصية ما زالت موجودة وتستخدم الـRig نفسه، وبصمة الـRig (معرّفات العظام وآباؤها ووضعية الراحة وخريطة Humanoid) لم تتغير؛ وإلا فالخطأ `job_stale`. بعد الإضافة تنتقل ملكية المقطع إلى سجل التراجع (المهمة لا تحتفظ بنسخة)، وإعادة تطبيق مهمة مطبّقة تعطي `job_applied`. **التراجع عن `job_apply` يعيد المهمة إلى `ready` مع مقطعها** فيمكن تطبيقها من جديد، والإعادة (Redo) تعيدها `applied` (D29).

**الملكية**: كل مهمة تنتمي إلى المصدر الذي بدأها (`CommandOrigin`: جلسة الوكيل، أو `ui` للمستخدم). عبر الأوامر يرى الوكيل مهامه فقط في القائمة، و`job_status`/`job_cancel`/`job_apply` على مهمة غيره تعطي `not_owner`. المستخدم (مصدر UI) يرى كل المهام ويستطيع إلغاءها وتطبيقها. واجهة C# بلا مصدر (`List()`، `Get(id)`، `Cancel(id)`) للتطبيق نفسه وغير مقيّدة.

**التنظيف**: تُحذف المهام المنتهية بعد 30 دقيقة من انتهائها (أو من تطبيقها)، ولا يُحتفظ بأكثر من 20 مهمة منتهية (الأقدم أولًا) — مع مقطعها غير المطبَّق إن وُجد. أي استثناء في خيط المهمة (حتى نفاد الذاكرة: `out_of_memory`) ينهيها بحالة `failed`، ولا تبقى مهمة `running` بعد انتهاء خيطها.
6. `clip.accept_draft {clipId}` (قابل للتراجع) يحوّل المسودة إلى مقطع عادي، والرفض بـ`clip.delete`.
7. `motion.providers {probe?}` (ReadOnly): التوفر، وهل وُجد الـrunner والملفات، وCPU/RAM/GPU قدر الإمكان ودون شبكة. `probe` يسأل الـrunner نفسه (Headless فقط)، **ولا ينتظر خلف توليد جارٍ**: إن كان الـrunner مشغولًا يعيد فورًا `runnerBusy: true` مع بيانات `hello` المحفوظة منذ بدء العملية. **الوكلاء لا يرون مسارات مطلقة**: تُعرض أسماء الملفات وأعلام `…Found` فقط، وتُستبدل المسارات المعروفة داخل الرسائل بأسماء ملفاتها (المسار الكامل يكشف اسم حساب المستخدم وبنية مجلداته).

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
- **`kimodo_runtime_options` (الخيوط والجهاز) يُتحقق من حجمه فقط ولا يُستخدم** في هذه النسخة. الجهاز يُختار من متغير البيئة `KIMODO_BACKEND` (`cpu` يفرض المعالج، وإلا Vulkan إن وُجد ثم CPU)، والخيوط من `KIMODO_THREADS`. لذلك يضبطهما `kimodo-runner` قبل التحميل. (منذ 0.11 يختار ترقيعنا الجهاز لكل مكوّن: §5.2.)
- **لا يوجد callback للتقدّم ولا إلغاء**: `kimodo_generate` استدعاء حاجب. لذلك يرسل الـrunner مراحل فقط (تحميل، توليد، إرسال)، والإلغاء = إنهاء العملية.
- الواجهة **لا تُرجع** أسماء المفاصل ولا الآباء ولا وضعية الراحة. وREADME يقول "query the joint count from the result rather than assuming a fixed skeleton".

**صيغة المخرجات** (من `src/motion_decode.cpp` و`scripts/export_glb.py`)
- الدوران المحلي = `transpose(G_parent) · G_joint` محوّلًا من تمثيل 6D العالمي، أي دوران نسبةً إلى الأب، **ووضعية الراحة دورانها هوية** (ملف GLB الذي يصدّره المشروع يكتب للعقد إزاحات فقط).
- `root_positions` هو الموضع المطلق لمفصل `Hips` (يُكتب مباشرة كـtranslation لعقدة Hips في GLB، وارتفاعه في الراحة 0.988 م).
- المعدل **30 إطارًا في الثانية** (`fps=30` افتراضيًا في `export_glb.py` و`motion_rep.hpp`).
- هيكل SOMA ‏(30): `Hips, Spine1, Spine2, Chest, Neck1, Neck2, Head, Jaw, LeftEye, RightEye, LeftShoulder, LeftArm, LeftForeArm, LeftHand, LeftHandThumbEnd, LeftHandMiddleEnd, Right… (مثلها), LeftLeg, LeftShin, LeftFoot, LeftToeBase, Right… (مثلها)` مع الآباء والإزاحات في `src/skeleton.hpp` (منقولة عن تعريفات NVIDIA بترخيص Apache-2.0). المحاور: ‎+Y أعلى، القدمان نحو ‎+Z، والجانب الأيسر على ‎+X، **مطابقة لمحاور Blazma** فلا تحويل. هذه البيانات مضمّنة في `ai/kimodo/src/soma_skeleton.hpp` و`src/Blazma.AI/SomaSkeleton.cs`، وBlazma يرفض أي هيكل مختلف يرسله الـrunner.
- غير منفّذ في kimodo.cpp: إدخال القيود (constraints)، والتوسيع إلى 77 مفصلًا، وتصدير GLB بشبكة مكسوّة. لذلك لا أصابع ولا وجه (R5)، وتُتجاهل القيود مع تحذير.

**البناء**: C++23، CMake 3.25+، Ninja. GGML وحدة فرعية مثبّتة (`ggml` عند `8c63e709`، الإصدار 0.20.2). Vulkan اختياري (`KIMODO_ENABLE_VULKAN`، افتراضيًا ON إن وُجدت الترويسات) مع CPU كاحتياط. لم يُذكر CUDA.

**الأوزان** (لا تُنزَّل تلقائيًا أبدًا، بل فقط بضغطة المستخدم — §5.1): منشورة على Hugging Face تحت منظمة `LocalAI-io`. أسماء الملفات: `kimodo-soma-rp-v1.1-f32.gguf`، `kimodo-soma-seed-v1.1-f32.gguf` (ومثلها لـG1)، ومُرمِّز النص `Llama-3-Kimodo-Q8_0.gguf` (الافتراضي) أو `Q6_K` / `Q5_K` / `Q4_K` / `Q4_K_M` / `BF16`، وكلها تشترك في `tokenizer.gguf`.

**الذاكرة**: العرض التجريبي يُبقي مُرمِّز Q8 كاملًا في VRAM بسقف 10 GiB على بطاقة 16 GiB. المكتبة افتراضيًا تنفّذ 8 طبقات في كل دفعة (`KIMODO_TEXT_LAYER_CHUNK` من 1 إلى 32) ويمكن فرض سقف بـ`KIMODO_TEXT_RESIDENT_LIMIT_MIB`. **لم نقِس الأداء ولا الذاكرة بعد، ولا يُعتمد أي رقم قبل القياس (R4).**

**ما تحققنا منه عمليًا في هذه المرحلة**
- بُني `kimodo-runner` على Linux ‏(GCC 13.3، CMake 3.28، Ninja، CPU فقط دون Vulkan) فوق kimodo.cpp المثبّت بالـcommit أعلاه، وجُرّب البروتوكول على الملف التنفيذي الحقيقي: `hello`، `diagnose`، خطأ ملف غير موجود (`models_not_found`)، ملف GGUF وهمي (`model_load_failed: truncated GGUF header`)، سطر غير صالح، `shutdown`. وجُرّب أيضًا من `KimodoProvider` في C#: التشخيص مع probe، والتقدّم، والخطأ النظيف.
- مراجعة المرحلة 7 (الإصدار 0.2.0): أُعيد بناء الـrunner الحقيقي وتأكدنا أن `frames: 4294967396` و`frames: 30.5` و`"30"` و`seed: -1` تُرفض بـ`invalid_request` مع `id` الطلب، وأن سطرًا بحجم 64 MiB يُرد عليه بـ`id: null` دون أن تزيد ذاكرة العملية (VmHWM) بأكثر من 1 MiB، وأن السطر غير الصالح يُرد عليه بالـ`id` المستخرج، وأن إغلاق stdin يُنهي العملية فورًا، وأن `PDEATHSIG` يقتل الـrunner عند قتل أبيه بـSIGKILL حتى مع بقاء stdin مفتوحًا في عملية أخرى.
- **غير متحقق منه**: توليد حقيقي (لا أوزان في البيئة ولم يُشغَّل أي نموذج)، البناء على Windows/MSVC وبناء Vulkan (يوجد workflow يدوي `.github/workflows/kimodo-runner.yml` لم يُشغَّل بعد)، الأداء والذاكرة، وصحة الاتجاهات على مخرجات حقيقية (اختُبرت على بيانات مصطنعة بالصيغة نفسها).

### بروتوكول `kimodo-runner` ‏(الإصدار 1)

- العملية: `kimodo-runner --stdio`. سطر JSON-RPC 2.0 واحد لكل رسالة (UTF-8، ينتهي بـ`\n`). stdout للبروتوكول فقط، وstderr للسجلات (يلتقط Blazma آخر 40 سطرًا ويقطع السطر عند 2000 حرف).
- حدود الحجم: الطلب ≤ 1 MiB (في الـrunner، ويُقرأ بحد: السطر الأطول يُتخطى دون تخزينه ويُرد عليه بخطأ `id: null`)، والرد ≤ 32 MiB افتراضيًا (في Blazma). تجاوز الحد أو سطر غير صالح من الـrunner ← تُنهى العملية ويُعاد `message_too_large` أو `protocol_error`.
- **التحقق الصارم من المدخلات (الإصدار 0.2.0 من الـrunner)**: `frames` و`steps` و`threads` و`seed` أعداد صحيحة فقط (تُرفض الكسور والنصوص والقيم المنطقية)، تُقرأ بـ64 بت ويُتحقق من مداها قبل أي تضييق: `frames` 1..10000، `steps` 1..1000، `threads` 0..1024، `seed` ≥ 0. (كانت `frames: 4294967396` تُقبل كـ100.) `KIMODO_BACKEND` و`KIMODO_THREADS` يُضبطان مع كل طلب، والخيوط جزء من مفتاح النموذج المحمّل.
- خطأ تحليل لطلب يُرد عليه بـ`id` الطلب إن أمكن استخراجه، وإلا `id: null`؛ وBlazma يُفشل الاستدعاء الجاري **فورًا** عند أي خطأ بلا `id` بدل انتظار المهلة.
- كذلك يتحقق Blazma من رد `generate` قبل أي حجز للذاكرة: `frames` ≤ 10000 و`joints` = 30، وضرب الأحجام بحساب مُتحقق (checked).

| الطريقة | الاتجاه | المعاملات | النتيجة |
|---|---|---|---|
| `hello` | طلب | `{protocol: 1, client}` | `{protocol, runner, version, kimodoAbi, skeletons, ggml, vulkanCompiled, backends, devices, deviceModes}` |
| `diagnose` | طلب | `{motionModel?, textModel?}` | `{ggml, vulkanCompiled, kimodoAbi, motionModelExists, textModelExists, modelLoaded, backends, devices, deviceModes, warnings}` |
| `generate` | طلب | `{prompt, frames, seed, steps, motionModel, textModel, device: auto\|gpu\|cpu\|hybrid, gpu?, threads}` | `{fps: 30, frames, joints: 30, skeleton: {key: "soma30", up: "+Y", forward: "+Z", names, parents, offsets}, rotations: [x,y,z,w,…] (T·J·4), rootPositions: [x,y,z,…] (T·3), model, textModel, steps, seed, runner, warnings, device: {mode, text, motion}}` |
| `progress` | إشعار من الـrunner | `{id, stage: load_model\|generate\|decode, fraction, message?}` | — |
| `cancel` | إشعار إلى الـrunner | `{id}` | أثناء التوليد يخرج الـrunner فورًا (`std::_Exit`)، وBlazma ينهي العملية أيضًا |
| `shutdown` | إشعار إلى الـrunner | `{}` | تخرج العملية (يُرسل عند الخمول) |

الأخطاء: `{"error": {"code": -32000, "message": "...", "data": {"code": "models_not_found" | "model_load_failed" | "generation_failed" | "device_unavailable" | "unsupported_skeleton" | "invalid_request" | "unknown_method"}}}`. يحتفظ الـrunner بالنموذج محمّلًا بين الطلبات (حسب المسارات والجهاز).

دورة الحياة في Blazma (`KimodoProvider`): لا تبدأ العملية إلا عند أول طلب، وتُوقف بعد 3 دقائق خمول (`shutdown` ثم إنهاء). توليد واحد في كل مرة. مهلات: البدء 20 ث، التشخيص 30 ث، التوليد 15 دقيقة (قابلة للتغيير). الانهيار يعطي `runner_crashed` مع رمز الخروج وآخر سطور السجل، والطلب التالي يبدأ عملية جديدة. الإلغاء والمهلة ينهيان العملية. فشل الـprobe يوقف **الـrunner الذي فحصه فقط** (وهو ما زال داخل القفل)، فلا يقتل توليدًا بدأ بعده. الـrunner الذي يكتمل بدؤه بعد `Dispose` أو بعد تغيير الإعدادات لا يُحتفظ به (`runner_stopped`)، و`Dispose` يلغي الاشتراك في `ProcessExit`.

**لا runner يتيم (D29)** — إذا مات Blazma (انهيار، `kill -9`، إنهاء من مدير المهام):
- الـrunner يقرأ stdin في خيط مستقل، فنهاية الإدخال (يُغلق الأنبوب حين تموت العملية الأم) تعني `std::_Exit` فورًا حتى في منتصف `kimodo_generate` الذي لا يمكن مقاطعته. طلب `generate` في الطابور بعد نهاية الإدخال لا يبدأ. الـrunner الوهمي في الاختبارات يفعل الشيء نفسه.
- Linux: يمرّر Blazma `KIMODO_RUNNER_PARENT_PID`، فيستدعي الـrunner ‏`prctl(PR_SET_PDEATHSIG, SIGKILL)` ثم يتحقق أن `getppid()` ما زال Blazma (سباق موت الأب قبل `prctl`). لأن Linux يقصد بـ"الأب" **الخيط** الذي أنشأ العملية، يبدأ Blazma كل runner من خيط مخصص يعيش ما عاشت العملية. إذا كان بينهما غلاف (سكربت بلا `exec`) يُلغى `PDEATHSIG` ويبقى الاعتماد على نهاية stdin.
- Windows: يُضاف الـrunner إلى Job object بـ`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (P/Invoke، خلف `OperatingSystem.IsWindows()`)، ومقبضه لا يُغلق أبدًا فيغلقه Windows عند موت Blazma فيموت الـrunner. **حدود `System.Diagnostics.Process`**: العملية توجد لحظة قبل إضافتها إلى الـJob (الـrunner لا يبدأ عمليات أخرى، ونهاية stdin تغطي هذه اللحظة)؛ ومع إعادة توجيه stdio يستدعي .NET ‏`CreateProcess` بـ`bInheritHandles = TRUE` فيرث الابن كل مقبض في Blazma معلَّم بأنه قابل للتوريث. .NET يفتح ملفاته ومقابسه غير قابلة للتوريث، لكن لا توجد في .NET واجهة لـ`PROC_THREAD_ATTRIBUTE_HANDLE_LIST` لاستبعاد مقابض فتحها كود أصلي (المحرك أو التعريفات).
- اختبار: `Runner_Exits_WhenItsHostIsKilled` يشغّل عملية مضيفة تبدأ الـrunner ثم يقتلها بـ`Kill` ويتحقق من خروج الـrunner خلال 5 ثوانٍ.

**بيئة الـrunner (D29)**: لا يرث الـrunner بيئة Blazma كاملة (رموز الوصول ومفاتيح الأدوات الأخرى). يمرّر Blazma فقط: `PATH`، `TEMP`/`TMP`/`TMPDIR`، `HOME`/`USERPROFILE`/`LOCALAPPDATA`/`APPDATA`، `SystemRoot`/`windir`/`SystemDrive`/`ProgramData`/`ProgramFiles`، `NUMBER_OF_PROCESSORS`، و`LD_LIBRARY_PATH` و`XDG_*` (بحث محمّل Vulkan عن الـICD على Linux)، و`DOTNET_ROOT` (runner مُدار عبر dotnet، كما في الاختبارات)، وكل ما يبدأ بـ`VK_` (محمّل Vulkan وتعريفاته) و`GGML_` (خيارات الواجهة الخلفية مثل `GGML_VK_VISIBLE_DEVICES`) و`CUDA_` (اختيار الجهاز). ثم يضيف ما يضبطه هو: `KIMODO_BACKEND=cpu` عند اختيار CPU، و`KIMODO_RUNNER_PARENT_PID` على غير Windows. أي `KIMODO_*` آخر في بيئة المستخدم (مثل `KIMODO_TEXT_RESIDENT_LIMIT_MIB`) **لا يُمرَّر**. **مسار الـrunner يجب أن يكون مطلقًا** (المسار النسبي يعتمد على مجلد العمل وترتيب البحث في `PATH`)؛ وإلا فالمزوّد غير متوفر مع رسالة واضحة.

**المسارات غير ASCII على Windows**: يحمل `kimodo-runner.exe` ملف manifest ‏(`ai/kimodo/src/kimodo-runner.manifest`) فيه `<activeCodePage>UTF-8</activeCodePage>` (Windows 10 1903+)، لأن kimodo.cpp يفتح الملفات بـ`std::ifstream(std::string)` الذي يستخدم صفحة ترميز ANSI؛ بدونه يفشل أي مسار خارجها (مثل مجلد مستخدم باسم عربي) بـ`cannot open GGUF file`. الـworkflow يختبر ذلك بنسخ الـrunner ونموذج وهمي إلى مجلد اسمه عربي/لاتيني/صيني ويتوقع `truncated GGUF header` (فُتح الملف) لا `cannot open`.

**سلسلة التوريد في البناء**: kimodo.cpp مثبّت بـcommit (وGGML بالوحدة الفرعية داخله)، وnlohmann/json يُنزَّل كأرشيف الإصدار v3.11.3 مع `URL_HASH SHA256=d6c65aca…757d`. في الـworkflow: كل Action مثبّت بـSHA الـcommit والوسم في تعليق (`actions/checkout` v4.2.2، `actions/upload-artifact` v4.6.2، `humbletim/install-vulkan-sdk` v1.2)، وإصدار Vulkan SDK ثابت `1.4.309.0` (بلا cache لأن خطوة الـcache داخل الـaction تستخدم `actions/cache` بوسم)، وكل رفع يحمل `SHA256SUMS.txt`. **ما زال غير مثبّت**: صور الـrunners المستضافة (مثبّتة على إصدار النظام `windows-2022`/`ubuntu-24.04` لا على بناء الصورة)، وحزم apt على Linux، ومُثبِّت Vulkan SDK الذي يُنزَّل من LunarG دون تحقق من checksum.

### الإعدادات

| الإعداد | متغير البيئة | الافتراضي |
|---|---|---|
| مسار `kimodo-runner` | `BLAZMA_KIMODO_RUNNER` | — (غير مثبّت) |
| مجلد النماذج (ويُبحث أيضًا في مجلده الفرعي `models`) | `BLAZMA_KIMODO_MODELS` | — |
| النموذج | `BLAZMA_KIMODO_MODEL` | `soma-rp-v1.1` (أو `soma-seed-v1.1`؛ G1 وSMPL-X غير مدعومين) |
| مُرمِّز النص (اسم ملف أو مسار) | `BLAZMA_KIMODO_TEXT_MODEL` | أول الموجود: Q8_0، Q6_K، Q5_K، Q4_K_M، Q4_K، BF16 |
| الجهاز | `BLAZMA_KIMODO_DEVICE` | `auto` (البطاقة أولًا مع انتقال تلقائي)، `gpu` (البطاقة فقط)، `cpu` (المعالج فقط)، `hybrid` (البطاقة + المعالج)؛ `vulkan` القديمة = `gpu` (§5.2) |
| بطاقة بعينها | `BLAZMA_KIMODO_GPU` | — (أفضل بطاقة)؛ اسم GGML كما يسرده الـrunner: `CUDA0`، `Vulkan1` |
| مهلة التوليد بالثواني | `BLAZMA_KIMODO_TIMEOUT_SECONDS` | 900 |

التطبيق يمرّر إعداداته عبر `MotionJobService.Kimodo.Settings = new KimodoSettings {…}`. **الوكلاء لا يستطيعون تغيير هذه الإعدادات** لأنها تحدد ملفًا تنفيذيًا (D28).

### تثبيت Kimodo يدويًا (المستخدم)

1. **بناء الـrunner**: من جذر المستودع: `cmake -S ai/kimodo -B ai/kimodo/build -G Ninja -DCMAKE_BUILD_TYPE=Release` (أضف `-DKIMODO_RUNNER_VULKAN=OFF` إذا لم يكن Vulkan SDK مثبّتًا) ثم `cmake --build ai/kimodo/build --target kimodo-runner`. الناتج في `ai/kimodo/build/bin/` مع مكتبات GGML بجانبه. على Windows: Visual Studio 2022 (MSVC) أو workflow ‏`kimodo-runner (optional)` اليدوي في GitHub Actions. يحتاج البناء إلى الإنترنت لجلب kimodo.cpp وGGML وnlohmann/json بالإصدارات المثبّتة.
2. **تنزيل الأوزان**: الأسهل من داخل البرنامج (§5.1). أو يدويًا: اقرأ بطاقات النماذج وتراخيصها أولًا، ثم نزّل من Hugging Face ‏`LocalAI-io/Kimodo-SOMA-RP-v1.1-GGML` الملف `models/kimodo-soma-rp-v1.1-f32.gguf`، ومن `LocalAI-io/Llama-3-Kimodo-GGML` الملفين `Llama-3-Kimodo-Q8_0.gguf` و`tokenizer.gguf` (أو استخدم `scripts/download_gguf_weights.sh` من kimodo.cpp). ضعها في مجلد واحد (أو في مجلد فرعي `models`).
3. اضبط `BLAZMA_KIMODO_RUNNER` (مسار مطلق) و`BLAZMA_KIMODO_MODELS` (أو من إعدادات التطبيق عندما تتوفر واجهتها)، ثم تحقق بـ`blazma-anim exec <file> motion.providers --arg probe=true`.

الأوزان والمجلدات `build` لا تُرفع إلى Git أبدًا (`.gitignore` يتجاهل `*.gguf` و`ai/kimodo/build*/`).

### 5.2 الأجهزة: بطاقة الرسوميات، المعالج، أو كلاهما (D44)

**ما يدعمه kimodo.cpp نفسه (الـcommit `5679ff1`)**: مكوّنان، كل منهما على **واجهة GGML خلفية واحدة** يختارها بنفسه: مُرمِّز النص (Llama-3 بحجم 8B، يُحمَّل في `kimodo_model_load`) ونموذج الحركة (يُحمَّل عند أول توليد). الاختيار في الكود الأصلي استدعاءات مباشرة: `ggml_backend_vk_init(0)` إن بُني مع Vulkan ووُجد جهاز، وإلا `ggml_backend_cpu_init()`، و`KIMODO_BACKEND=cpu` يفرض المعالج لكليهما. **لا CUDA، ولا `ggml_backend_dev`/سجل الأجهزة، ولا `ggml_backend_sched`** (لا تقسيم للرسم بين جهازين، ولا رجوع للمعالج في العمليات غير المدعومة)، وCMake يفرض `GGML_BACKEND_DL=OFF`. `kimodo_runtime_options.device` لا يُستخدم.

**ما أضفناه (ترقيع صغير في `ai/kimodo/patches/`، يُطبَّق وقت الـconfigure على نسخة البناء، والمصدر الأصلي لا يُمس)**: الدالتان السابقتان تُستبدلان باختيار من سجل أجهزة GGML (`kimodo_backend_select.hpp`): `KIMODO_TEXT_DEVICE` و`KIMODO_MOTION_DEVICE` تسمّيان جهازًا بعينه (`CUDA0`، `Vulkan0`، `CPU`)؛ إن لم يوجد أو فشل تهيئته يفشل التحميل برسالة (والقرار لـBlazma)؛ وبدونهما السلوك الأصلي «البطاقة أولًا» (CUDA ثم أي بطاقة منفصلة ثم المدمجة ثم المعالج). وCMake لا يفرض `GGML_BACKEND_DL` ولا يربط مكتبات الواجهات مباشرة. هذا كل التغيير.

**البناء**: runner واحد بـ`GGML_BACKEND_DL=ON` و`GGML_CPU_ALL_VARIANTS=ON` (x64): بجانب `kimodo-runner.exe` توجد `ggml-cpu-<variant>.dll` (من SSE4.2 إلى AVX-512؛ يُحمَّل أفضلها للمعالج)، `ggml-vulkan.dll` (أي بطاقة لها تعريف Vulkan: NVIDIA وAMD وIntel)، و`ggml-cuda.dll` مع `cudart64_12.dll` و`cublas64_12.dll` و`cublasLt64_12.dll` (CUDA 12.4، تُعاد توزيعها بموجب EULA الخاصة بـNVIDIA، `legal/NVIDIA_CUDA_NOTICE.txt`). `ggml_backend_load_all()` يحمّل ما يعمل منها: بلا تعريف NVIDIA (`nvcuda.dll`) لا يُحمَّل CUDA، وبلا محمّل Vulkan (`vulkan-1.dll`، يأتي مع تعريف البطاقة) لا يُحمَّل Vulkan، والمعالج يعمل دائمًا. بناء Vulkan على Windows كان يفشل عند `find_package(SPIRV-Headers)`؛ تُجلب الآن KhronosGroup/SPIRV-Headers مثبّتة بالـcommit `09913f08` (وسم `vulkan-sdk-1.4.309.0`) عبر `FetchContent … OVERRIDE_FIND_PACKAGE`. لا ملفات CUDA على Linux (Vulkan + المعالج فقط).

**الأوضاع** (`device` في `generate`، و`KimodoSettings.Device`):

| الوضع | في الواجهة | مُرمِّز النص | نموذج الحركة |
|---|---|---|---|
| `auto` | تلقائي (البطاقة أولًا) | البطاقة | البطاقة — ثم الانتقال أدناه |
| `gpu` | بطاقة الرسوميات فقط | البطاقة | البطاقة؛ يفشل `device_unavailable` بلا بطاقة |
| `cpu` | المعالج فقط | المعالج | المعالج |
| `hybrid` | بطاقة الرسوميات + المعالج | المعالج | البطاقة — لبطاقات ذاكرتها لا تكفي مُرمِّز النص؛ خطوات الانتشار المتكررة على البطاقة |

`gpu` اختياري: اسم الجهاز (يُرفض إن لم يكن بطاقة موجودة). الـrunner يعيد في `device` الأجهزة التي استُخدمت فعلًا (`{mode, text: {name, description, type, backend, memoryTotal, memoryFree}, motion: {…}}`)، و`hello`/`diagnose` يسردان `devices` بالشكل نفسه و`backends` المحمّلة — مثل `llama.cpp --list-devices`.

**الانتقال التلقائي في C# (`KimodoDevicePlanner`)** — الفكرة نفسها في Blazma AI (الأحدث أولًا ثم الأقدم ثم المعالج): Auto = `gpu` على أول جهاز CUDA، ثم `gpu` على أول جهاز Vulkan (قد تكون البطاقة نفسها)، ثم المدمجة إن لم توجد منفصلة، ثم `hybrid` على أفضل بطاقة، ثم `cpu`. «البطاقة فقط» = محاولات البطاقة فقط، و«البطاقة + المعالج» = `hybrid` على كل بطاقة. الانتقال إلى المحاولة التالية عند: `device_unavailable`، `model_load_failed` (مثل نفاد ذاكرة البطاقة عند التحميل)، `generation_failed`، `runner_crashed` (انهيار داخل تعريف البطاقة: يُبدأ runner جديد)، `invalid_motion`، `internal_error`. **لا انتقال** عند الإلغاء أو انتهاء المهلة أو مدخلات خاطئة أو ملفات ناقصة. كل انتقال يُسجَّل ويضيف تحذيرًا أول النتيجة: «Generated on … after N failed attempt(s): Kimodo could not run on … [code]: … — trying … instead.» (يُعرض بالعربية في الواجهة)، وتقدّم بمرحلة `fallback`. بيانات المقطع: `kimodo.device`، `kimodo.generatedOn`، `kimodo.motionDevice`، `kimodo.textDevice`؛ و`motion.job_apply` يعيد `device`/`motionDevice`/`generatedOn`.

**الواجهات البرمجية**: `KimodoProvider.RunnerDevices` (من آخر `hello`)، `ProbeDevicesAsync`، `LastDeviceUse`؛ و`motion.providers` و`ai.hardware` يضيفان `kimodoDevices` و`kimodoDeviceMode` و`kimodoGpuDevice` و`kimodoLastDevice`. runner أقدم (0.2، بلا `devices`) يعمل كما كان: `auto`/`cpu`/`vulkan`، و`hybrid` يُرفض بطلب التحديث.

**المقترح (`HardwareAdvisor`)**: `auto` حين تكفي البطاقة **أو حين لا تُقرأ ذاكرة بطاقة منفصلة** (كان يضع `cpu` هنا فيبقى Kimodo على المعالج رغم وجود بطاقة جيدة — سبب البطء المُبلَّغ عنه)، و`hybrid` لبطاقة منفصلة ذاكرتها أقل من حد الحزمة، و`cpu` بلا بطاقة منفصلة. الإعداد `cpu` المحفوظ قبل 0.11 يعود `auto` مرة واحدة.

**ما تحقق منه CI** (لا بطاقة رسوميات على أجهزته): البناء على Windows (MSVC، Vulkan SDK 1.4.309.0، وCUDA 12.4 في وظيفة منفصلة) وLinux (GCC 13، Vulkan)؛ تحميل `ggml-cpu-*` و`ggml-vulkan` فعليًا، وأن اعتماديات `ggml-cuda.dll` كلها مشحونة عدا `nvcuda.dll` (من التعريف)؛ `diagnose` يسرد المعالج، ومع lavapipe ‏(Vulkan برمجي على Linux) يسرد `Vulkan0`، و`hybrid` يضع الحركة على `Vulkan0` والنص على المعالج؛ `gpu`/`hybrid` بلا بطاقة → `device_unavailable`؛ المسارات غير ASCII. **غير متحقق منه**: توليد حقيقي على بطاقة NVIDIA/AMD/Intel (CUDA أو Vulkan)، دعم عمليات kimodo كلها في CUDA، والأداء والذاكرة — يجب أن يجرّبه المستخدم.

### 5.1 تنزيل النموذج بضغطة واحدة (D43)

**ما هو موجود فعلًا على Hugging Face** (تحقّقنا عبر واجهة `huggingface.co/api` في 2026-11-06؛ الأحجام والـSHA-256 من بيانات LFS ومن `MANIFEST.json` في كل مستودع، والروابط مثبّتة بالـcommit):

| المستودع (commit) | الملفات | الحجم |
|---|---|---:|
| `LocalAI-io/Kimodo-SOMA-RP-v1.1-GGML` (`65f8b8ab`) | `models/kimodo-soma-rp-v1.1-f32.gguf` — **F32 فقط، لا نسخ مضغوطة** | 1.13 GB |
| `LocalAI-io/Kimodo-SOMA-SEED-v1.1-GGML` (`4d88f95f`) | `models/kimodo-soma-seed-v1.1-f32.gguf` (غير مستخدم في الحزم حاليًا) | 1.13 GB |
| `LocalAI-io/Llama-3-Kimodo-GGML` (`f583f3ee`) | `Q4_K` 4.39، `Q4_K_M` 5.06، `Q5_K` 5.33، `Q6_K` 6.32، **`Q8_0` 8.14 (الموصى به)**، `BF16` 15.18، و`tokenizer.gguf` 7.3 MB مشترك، و`LICENSE-META-LLAMA-3.txt` و`NOTICE` | — |

ومستودعات G1 وSMPL-X موجودة أيضًا لكنها غير مدعومة (D7). الضغط إذن في **مُرمِّز النص فقط**، فالحزم تختلف فيه:

| الحزمة | مُرمِّز النص | التنزيل | الذاكرة (أدنى / موصى) | VRAM للبطاقة (أدنى / موصى) | لمن |
|---|---|---:|---|---|---|
| `light` خفيف | `Q4_K_M` («تجربة منخفضة البت المفضّلة» عند المصدر) | 6.2 GB | 7.5 / 16 GB | 6 / 8 GB | الأجهزة الضعيفة، المعالج |
| `standard` قياسي | `Q8_0` (افتراضي kimodo.cpp) | 9.3 GB | 12 / 16 GB | 8 / 12 GB | بطاقة رسوميات 12 GB+ |
| `reference` مرجعي | `BF16` | 16.3 GB | 20 / 32 GB | 16 / 20 GB | الأجهزة القوية جدًا |

كل حزمة = نموذج الحركة `soma-rp-v1.1` + مُرمِّز النص + `tokenizer.gguf` + ملفات الترخيص (`LICENSE-META-LLAMA-3.txt`، `NOTICE-Llama-3-Kimodo.txt`، `NOTICE-Kimodo-SOMA.txt`)، في مجلد واحد. **أرقام الذاكرة تقديرية** (حجم الأوزان + نحو 1 GiB) وليست قياسًا (R4)، لذلك تبقى كل الحزم قابلة للاختيار.

**التراخيص** (تُعرض قبل التنزيل ويجب قبولها): نموذج SOMA تحت **NVIDIA Open Model License** (الاستخدام التجاري مسموح، والتحويل إلى GGUF لا يمنح حقوقًا إضافية)؛ مُرمِّز النص مبني على Meta Llama 3 تحت **Meta Llama 3 Community License** ("Built with Meta Llama 3"، مع محوّلات LLM2Vec بترخيص MIT). الروابط في `ModelCatalog.json`.

**كشف العتاد** (`HardwareProbe`): Windows من مفتاح فئة محوّلات العرض في السجل `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\NNNN` (`DriverDesc`، `ProviderName`، `MatchingDeviceId` للمصنّع، والذاكرة من `HardwareInformation.qwMemorySize` ثم `HardwareInformation.MemorySize` كـDWORD أو binary)، مع تخطي «Microsoft Basic Display/Render» والمحوّلات البعيدة/الافتراضية (`root\`، `swd\`). Linux من `/sys/class/drm` و`/proc/driver/nvidia`. وإن وُجد `nvidia-smi` في `PATH` يُشغَّل مرة واحدة (مهلة 5 ث) للحصول على اسم وVRAM دقيقين لبطاقات NVIDIA. «أفضل بطاقة» = المنفصلة قبل غير المعروفة قبل المدمجة، ثم الأكبر ذاكرة. (سابقًا لم يكن هناك كشف على Windows إطلاقًا، وهذا سبب «البرنامج لا يرى كرت الشاشة».)

**الاقتراح** (`HardwareAdvisor.Recommend`): أكبر حزمة تتوفر لها الذاكرة الموصى بها، وإن كانت تحتاج بطاقة (`standard`، `reference`) فبطاقة منفصلة بالـVRAM الموصى به؛ وإلا `light`. الجهاز `auto` (Vulkan إن وُجد في الـrunner وإلا CPU) حين تكفي البطاقة، وإلا `cpu`. السبب بالعربية والإنجليزية، وكل حزمة معلَّمة بـ`fits`/`gpuFits`.

**المنزِّل** (`ModelDownloader`):
- يبدأ **فقط** عند ضغط المستخدم بعد عرض `Plan` (الحجم، المساحة الحرة، التراخيص، الملفات المتعارضة)، و`DownloadAsync` يرفض (`licence_not_accepted`) إن لم تُمرَّر معرّفات التراخيص المقبولة.
- HTTPS فقط إلى `huggingface.co`، وإعادة التوجيه تُتبع يدويًا (≤ 10) وفقط إلى HTTPS على `*.huggingface.co` أو `*.hf.co` (مثل `cas-bridge.xethub.hf.co` و`us.aws.cdn.hf.co` التي يعيد إليها HF ملفات Xet فعليًا)؛ غير ذلك `redirect_refused`.
- الاستئناف: `<file>.part` + ‏`Range`؛ يُعاد حساب SHA-256 للجزء الموجود، وإن تجاهل الخادم الـRange يبدأ من الصفر. الإلغاء يُبقي الـ`.part`.
- فحص المساحة قبل البدء (`disk_full`)، ثم SHA-256 بعد التنزيل (`hash_mismatch` يحذف الملف)، ثم `File.Move` ذري. الملف الموجود والصحيح يُتخطى دون تنزيل، والموجود المختلف لا يُستبدل إلا بخيار `ReplaceMismatchedFiles` بعد سؤال المستخدم (`file_conflict`).
- أحداث `Progress`: المرحلة (Checking/Downloading/Verifying/Completed)، الملف ورقمه، البايتات المنجزة/الكلية للحزمة، السرعة (متوسط آخر 5 ث) والوقت المتبقي، بحد أقصى ~5 مرات/ث، على خيط التنزيل (الواجهة تنقلها إلى الخيط الرئيسي).
- المجلد الافتراضي `ModelCatalog.DefaultModelsDirectory()`: ‏`%LOCALAPPDATA%\BlazmaAnimation\models` على Windows (غير متجوّل لأنها GBs)، و`$XDG_DATA_HOME/BlazmaAnimation/models` أو `~/.local/share/...` على Linux. في الوضع المحمول تمرّر الواجهة `userdata/models`.
- بعد النجاح: `ModelInstallResult.ApplyTo(settings)` يضبط المجلد ونموذج الحركة واسم ملف مُرمِّز النص والجهاز. (إعدادات التطبيق الحالية تحفظ المجلد والجهاز؛ يلزم حفظ `TextModel` أيضًا حتى لا يُختار ملف آخر موجود في المجلد نفسه — الأفضلية Q8_0 ثم Q6_K…)

**الـrunner لا يُنزَّل**: المستودع `mr-kateba/blazma-Animation` خاص، وروابط ملفات إصداراته (`github.com/.../releases/download/v<ver>/...`) تعطي 404 بلا تسجيل دخول (تحققنا). لذلك يُضمَّن `kimodo-runner` داخل البرنامج في `tools/kimodo/` (zip والمثبّت وtar.gz/AppImage): `scripts/build-release.sh` يأخذه من `KIMODO_RUNNER_WIN`/`KIMODO_RUNNER_LINUX` أو `build/cache/kimodo-runner-win64` و`build/cache/kimodo-runner-linux-x64` (مجلد أو zip من مخرجات workflow ‏`kimodo-runner (optional)`)، يتحقق من `SHA256SUMS.txt`، ويرفض أي `.gguf`، وينشر أيضًا `kimodo-runner-<ver>-win64.zip` و`-linux-x64.tar.gz`. في GitHub: شغّل workflow الـrunner يدويًا ثم Release مع `kimodo_runner_run_id`. التطبيق يجده بـ`ModelCatalog.Default.FindBundledRunner(appDir)`. تراخيصه (kimodo.cpp Apache-2.0، GGML MIT، nlohmann/json MIT) في `THIRD_PARTY_NOTICES.md`.

**للوكلاء**: `ai.hardware` و`ai.models` للقراءة فقط (العتاد، الحزم، ما يناسب الجهاز، ما هو مثبّت؛ دون مسارات مطلقة). **لا أمر تنزيل للوكلاء**: التنزيل GBs على حساب المستخدم وقرار قبول تراخيص طرف ثالث، فهو قرار المستخدم وحده (D43).

**واجهة النواة للـUI**: `ModelCatalog.Default` (`Tiers`، `Licences`، `Runner`، `State(tier, dir)`، `DefaultModelsDirectory()`، `FindBundledRunner`)، `HardwareProbe.Probe(refresh)` و`HardwareReport.BestGpu`، `HardwareAdvisor.Recommend(hw, catalog)`، `new ModelDownloader()` ← `Plan(tier, dir)` ← `DownloadAsync(tier, dir, acceptedLicenceIds, device, ct)` مع الحدث `Progress` و`ModelDownloadException.Code`.

### خطة الدمج (الحالة)
1. ✅ `kimodo-runner` (C++ يغلّف واجهة C) كعملية منفصلة بـJSON-RPC عبر stdio — مبني على Linux، وWindows عبر workflow غير مُجرَّب.
2. ✅ المستخدم يحدد المسارات يدويًا، أو ينزّل حزمة بضغطة واحدة من الإعدادات (§5.1، D43). **لا تنزيل تلقائي ولا في الخلفية.**
3. ✅ SOMA ‏(30 مفصلًا) ← هيكل Blazma القياسي عبر `SomaSkeleton.ToStandard` المختبر على الـMannequin وعلى RiggedFigure وCesiumMan.
4. ✅ الناتج مفاتيح خطية قابلة للتحرير مع اختزال بسماحية، ويُحفظ Prompt وSeed والإعدادات في `Metadata`.
5. ✅ اختبار القبول (بالمزوّد الإجرائي): توليد ← مسودة ← قبول ← تعديل مفتاح ← تصدير GLB ← تحقق (`Acceptance_TextToMotion_Keys_Edit_Export`). يبقى تكراره بأوزان Kimodo حقيقية على جهاز المستخدم.

## 6. الوثائق الموجّهة للوكلاء (تُكتب في المرحلة 5)
`AI_GUIDE.md`، `MCP_TOOLS.md` (مولّدة تلقائيًا)، `CLI_REFERENCE.md` (مولّدة تلقائيًا)، `ANIMATION_SCHEMA.md`، `AGENT_RECIPES.md`.
