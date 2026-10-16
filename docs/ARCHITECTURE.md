# المعمارية التقنية — Blazma Animation

> الحالة: مسودة المرحلة 0، بانتظار الموافقة. أي بند هنا لم يُنفَّذ ولم يُختبر بعد.

## 1. القرار التقني الأساسي

| الطبقة | التقنية | السبب |
|---|---|---|
| الواجهة والـViewport | **Godot 4.7.x (نسخة .NET)** | أحدث إصدار مستقر تم التحقق منه: `4.7.2-stable`. يوفر عرضًا ثلاثي الأبعاد وVulkan وواجهة Compatibility (OpenGL) للأجهزة الضعيفة. |
| نواة الأنيميشن | **C# / .NET (مكتبات مستقلة لا تعتمد على Godot)** | تُختبر بـ`dotnet test` دون واجهة، وتُشارَك حرفيًا بين التطبيق وCLI وMCP. |
| الحسابات الثقيلة/المكتبات الخارجية | **C/C++ عند الحاجة فقط** | مثل kimodo.cpp عبر عملية منفصلة. |
| CLI | تطبيق .NET Console باسم `blazma-anim` | يستخدم المكتبات نفسها. |
| MCP | خادم .NET عبر stdio | يستخدم سجل الأوامر نفسه. لا يوجد منطق تنفيذ مكرر. |

**قاعدة صارمة:** المكتبات `Blazma.Core/Rigging/Motion/Physics/Combat/Export/AI` لا تحتوي على أي `using Godot`.
يحوّل Godot البيانات إلى عُقَده (Skeleton3D وMeshInstance3D وغيرها) عبر طبقة Adapter رفيعة داخل `Blazma.UI`.

### ما يجب التحقق منه في بداية المرحلة 1 (لم يتم بعد)
- نسخة .NET التي يتطلبها Godot 4.7 للتصدير على Windows وLinux.
- تشغيل Godot بوضع `--headless` لاختبارات الواجهة ولقطات الشاشة داخل CI.
- عمل واجهة Compatibility على بطاقات رسومية قديمة.
- **ملاحظة بيئية:** بيئة العمل الحالية (حاوية سحابية Linux) لا تحتوي على Godot ولا على .NET SDK. يجب تثبيتهما أو إعداد سكربت إعداد للبيئة قبل المرحلة 1.

## 2. الوحدات والاعتماديات

```
Blazma.Core  ←  Blazma.Rigging  ←  Blazma.Motion  ←  Blazma.Physics
     ↑                ↑                  ↑                 ↑
     └──── Blazma.Export, Blazma.Combat (اختيارية), Blazma.AI
                              ↑
                     Blazma.Commands  (سجل الأوامر + Undo/Redo + الصلاحيات)
                     ↑        ↑          ↑
               Blazma.UI   blazma-anim   blazma-mcp
               (Godot)       (CLI)        (MCP)
```

- الاتجاه دائمًا من الأعلى (الواجهات) إلى الأسفل (النواة)، ولا يُسمح بالعكس.
- `Blazma.Combat` وحدة اختيارية: لا تعتمد عليها أي وحدة أخرى، وتسجّل أوامرها كـPlugin.
- `Blazma.Commands` منفصلة عن `Blazma.Agent` حتى تستخدمها الواجهة دون جلب كود MCP.

| الوحدة | المسؤولية |
|---|---|
| Blazma.Core | نموذج المشروع، المشاهد، الهيكل العظمي، التحويلات، المقاطع، المفاتيح، المنحنيات، التسلسل (Serialization)، أوامر التعديل |
| Blazma.Rigging | Skeleton mapping، FK، Two-bone IK، القيود، Retargeting، فحص الـRig |
| Blazma.Motion | Interpolation، Blending، Root motion، Loop، Retiming، التنظيف، كشف انزلاق القدم |
| Blazma.Physics | مركز الكتلة، Support polygon، التوازن، المسارات الباليستية، الحركة الثانوية |
| Blazma.Combat | Hitboxes، Hurtboxes، أحداث الهجوم، نوافذ الإلغاء |
| Blazma.Export | glTF/GLB، JSON sidecar، Godot، ثم Unity/Unreal/FBX |
| Blazma.AI | `IMotionGenerationProvider`، Kimodo adapter، إدارة المسودات، خط التحقق |
| Blazma.Agent | MCP server، CLI، الجلسات، الصلاحيات |
| Blazma.UI | واجهة Godot بالكامل |

## 3. طبقة الأوامر الموحدة (Command Layer)

كل تعديل يمر عبر المسار نفسه:

```
UI         → CommandBus.Execute(cmd) → Core
Claude/MCP → CommandBus.Execute(cmd) → Core
Codex/CLI  → CommandBus.Execute(cmd) → Core
```

```csharp
public interface ICommand {
    string Id { get; }                 // "keyframe.add"
    CommandEffect Effect { get; }      // ReadOnly | Mutating | Destructive | External
    CommandResult Execute(CommandContext ctx);
    void Undo(CommandContext ctx);     // إلزامي إذا كان Effect != ReadOnly
}

public sealed record CommandResult(
    bool Ok, JsonNode? Data, IReadOnlyList<Change> Changes,
    IReadOnlyList<Diagnostic> Warnings, ErrorInfo? Error);
```

- **سجل الأوامر (Command Registry):** كل أمر يعلن اسمه، مخطط مدخلاته (JSON Schema)، مخطط مخرجاته، تأثيره، ووصفه.
  تُولَّد منه تلقائيًا: أدوات MCP، أوامر CLI الفرعية، ووثيقة `MCP_TOOLS.md`. **مصدر الحقيقة واحد.**
- **Undo/Redo:** مكدّس أوامر لكل مشروع. العمليات المركبة (مثل توليد حركة بالـAI) تُجمَّع في `CompoundCommand` واحد يمكن التراجع عنه بخطوة واحدة.
- **سجل العمليات:** كل أمر يُسجَّل في `metadata/oplog.jsonl` داخل المشروع، مع المصدر (ui/cli/mcp) والزمن والنتيجة.
- **الإشعار بالتغيير:** `Changes` تحدد ما تغيّر (مقطع، مسار، نطاق زمني)، فتعيد الواجهة رسم الأجزاء المتأثرة فقط ولا تعيد حساب الحركة كاملة.

## 4. نموذج العمليات (Process Model)

| الوضع | الوصف |
|---|---|
| تطبيق مفتوح | يفتح Godot خادم IPC محليًا (Named Pipe على Windows، Unix socket على Linux). عند تشغيل `blazma-mcp` أو `blazma-anim --attach` يتصل بالتطبيق ويرسل الأوامر نفسها، فتظهر التعديلات مباشرة مع Undo. |
| بدون واجهة (Headless) | CLI/MCP يحمّلان المشروع مباشرة عبر المكتبات وينفذان الأوامر ثم يحفظان. يُمنع فتح المشروع نفسه من عمليتين للكتابة عبر ملف قفل `.lock`. |
| توليد الحركة | `kimodo-runner` عملية منفصلة (C++) يُطلقها التطبيق عند الطلب فقط. الانهيار أو نفاد الذاكرة لا يُسقط المحرر، والإلغاء يتم بإنهاء العملية. لا تُحمَّل أي نماذج أثناء الخمول. |

الاتصال بين الأطراف بـJSON-RPC 2.0 مع الرسائل نفسها المستخدمة في MCP، فلا توجد صيغة ثالثة.

## 5. الرياضيات والاصطلاحات

- **الوحدة:** المتر. **المحاور:** نظام يميني (right-handed)، ‎+Y للأعلى، ‎+Z لأمام الشخصية، مثل glTF 2.0، وهو متوافق مع Godot.
- **الدوران:** Quaternion بترتيب `(x, y, z, w)` ومُطبَّع. زوايا Euler للعرض فقط وبترتيب XYZ قابل للتغيير في الواجهة.
- **الزمن:** عدد صحيح 64-bit بوحدة *flick* (‏1/705,600,000 ثانية). يقبل القسمة على 24 و25 و30 و48 و50 و60 و90 و100 و120 إطارًا في الثانية، فلا تتراكم أخطاء عشرية عند تغيير FPS.
- **السماحيات الرقمية الافتراضية للاختبارات:** موضع ‎1e-5 م، دوران ‎1e-4 راديان (زاوية بين Quaternions)، زمن ‎0 flick بعد الحفظ والتحميل، و≤ نصف إطار بعد التصدير.

## 6. الاختبارات

| النوع | الأداة |
|---|---|
| Unit | xUnit على مكتبات .NET دون Godot |
| Integration | xUnit + CLI حقيقي (تشغيل العملية وفحص JSON و exit code) + عميل MCP اختباري |
| Visual | Godot headless يلتقط لقطات شاشة ويقارنها بصور مرجعية بسماحية |
| Round-trip | تصدير GLB ← استيراد ← مقارنة الهيكل والمدة والتحويلات والأحداث |
| Regression | مشاريع عينة محفوظة بكل إصدار من المخطط تُرحَّل وتُفحص |
