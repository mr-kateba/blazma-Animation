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

## 4. واجهة مزوّد الحركة

```csharp
public interface IMotionGenerationProvider {
    ProviderInfo Info { get; }        // الاسم، الترخيص، القدرات، الهياكل المدعومة
    HardwareReport Probe();           // CPU/Vulkan/VRAM/RAM، وتوفر النموذج
    Task<MotionResult> GenerateAsync(MotionRequest req,
        IProgress<GenerationProgress> progress, CancellationToken ct);
}
```

`MotionResult` يحتوي على دورانات محلية للمفاصل ومسار الجذر وFPS واسم الهيكل المصدر. يتولى `Blazma.Rigging` تحويله إلى هيكل المستخدم.

## 5. kimodo.cpp — ما تم التحقق منه

المصدر: <https://github.com/localai-org/kimodo.cpp> (تمت مراجعته في 2026-10-16)

- منفذ C++/GGML لنموذج NVIDIA Kimodo. **ترخيص الكود Apache-2.0**، وGGML والأوزان لها تراخيص منفصلة.
- واجهة C في `include/kimodo/kimodo_capi.h`، وتشمل `kimodo_generate` (نص) و`kimodo_generate_embedding` (متجه بطول 4096).
- البناء: C++23، CMake 3.25+، Ninja. المعالجات المدعومة: **CPU وVulkan** (لم يُذكر CUDA).
- المخرجات: دورانات محلية XYZW بشكل `[T, J, 4]` ومواضع الجذر `[T, 3]` بمعدل **30 إطارًا في الثانية**، وملف `animation.glb` للهيكل فقط.
- **مهم:** واجهة C **لا تُرجع** أسماء المفاصل ولا آباءها ولا وضعية الراحة ولا اتجاه المحاور. لذلك يجب أن يحمل `kimodo-runner` تعريف هيكل SOMA كاملًا (الأسماء، الآباء، وضعية الراحة، المحور العلوي)، ويعيد عيّنة الحركة إلى FPS المقطع. حد الـ30 مفصلًا (الجسم فقط) يُذكر صراحة في `MotionResult`.
- الهياكل:
  - **SOMA RP/SEED v1.1** (‏30 مفصلًا) و**G1** (‏34 مفصلًا، روبوت Unitree): NVIDIA Open Model License، وتسمح بالاستخدام التجاري حسب الصفحة. **يجب مراجعة النص الكامل للترخيص قبل التوزيع.**
  - **SMPL-X RP v1**: ترخيص بحث داخلي غير إنتاجي فقط. **مستبعد من أي توزيع.**
  - مُرمِّز النص يتضمن مكونات Meta Llama 3 بشروطها الخاصة، وتحتاج مراجعة مستقلة.
- الذاكرة: العرض التجريبي يستخدم حدًا أقصى نحو 10 GiB VRAM لمُرمِّز Q8. يمكن تخفيضه عبر `KIMODO_TEXT_RESIDENT_LIMIT_MIB` والتكميم الأقل (q4_k). **لم نقِس الأداء بعد، ولا يُعتمد أي رقم قبل القياس.**

### خطة الدمج
1. بناء `kimodo-runner` (C++ صغير يغلّف واجهة C) كعملية منفصلة تتواصل بـJSON-RPC عبر stdio.
2. المستخدم يحدد مسار ملفات النموذج يدويًا من الإعدادات. **لا تنزيل تلقائي.**
3. هيكل SOMA ‏(30 مفصلًا) يُطابَق مع هيكل Blazma القياسي عبر `humanoidMap` مُعدّ مسبقًا ومختبر.
4. الناتج يتحول إلى مفاتيح قابلة للتحرير (اختزال المفاتيح بسماحية)، ويُحفظ Prompt وSeed والإعدادات.
5. اختبار القبول: توليد ← Retarget ← تعديل يدوي ← تصدير GLB ← إعادة استيراد ← مقارنة.

## 6. الوثائق الموجّهة للوكلاء (تُكتب في المرحلة 5)
`AI_GUIDE.md`، `MCP_TOOLS.md` (مولّدة تلقائيًا)، `CLI_REFERENCE.md` (مولّدة تلقائيًا)، `ANIMATION_SCHEMA.md`، `AGENT_RECIPES.md`.
