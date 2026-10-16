# صيغة البيانات — `.blazanim`

> مسودة المرحلة 0. رقم المخطط الأول: `schemaVersion: 1`.

## 1. الحاوية

ملف `.blazanim` هو أرشيف ZIP (الضغط: Deflate للـJSON، وStore للملفات المضغوطة أصلًا مثل PNG/GLB):

```
project.json          البيانات الوصفية، رقم المخطط، الفهارس
scenes/<id>.json      المشاهد والأجسام
rigs/<id>.json        الهياكل العظمية وخرائط العظام
clips/<id>.json       مقاطع الأنيميشن
assets/               ملفات مستوردة (GLB...) أو مراجع نسبية لها
metadata/oplog.jsonl  سجل العمليات
metadata/ai/<id>.json   Prompt وSeed وإعدادات التوليد لكل مسودة AI
previews/             صور مصغّرة
```

**لماذا ZIP وليس مجلدًا؟** ملف واحد أسهل للمستخدم في النقل والمشاركة. أثناء العمل يُفك المشروع إلى مجلد جلسة،
والحفظ يعيد بناء الأرشيف.

## 2. الحفظ الذري والاستعادة

1. كتابة الأرشيف إلى `name.blazanim.tmp` في المجلد نفسه.
2. `fsync`، ثم التحقق من الأرشيف بإعادة قراءة `project.json` ومطابقة checksums (SHA-256 لكل ملف داخل `project.json`).
3. استبدال ذري: `File.Replace` على Windows و`rename` على Linux، مع الاحتفاظ بالنسخة السابقة باسم `name.blazanim.bak`.
4. **الحفظ التلقائي:** كل N دقائق (قابل للضبط، الافتراضي 3) إلى مجلد الاستعادة في بيانات المستخدم المحلية، وليس فوق ملف المستخدم.
5. **الاستعادة بعد الانهيار:** عند الإقلاع، إذا وُجد autosave أحدث من الملف الأصلي مع علامة "جلسة لم تُغلق بشكل سليم"، يُعرض على المستخدم الاستعادة أو التجاهل.

## 3. المعرّفات والمسارات

- كل كائن له `id` ثابت (ULID نصي) لا يتغير بإعادة التسمية.
- مسارات الأصول نسبية لموقع ملف المشروع، أو داخلية في `assets/`.

## 4. الترحيل (Migrations)

- `schemaVersion` عدد صحيح. لكل انتقال `n → n+1` دالة ترحيل نقية مع اختبار على ملف عينة محفوظ في `tests/fixtures/schema-v<n>/`.
- يُرفض فتح ملف بمخطط أحدث من البرنامج، مع رسالة واضحة، ولا يُحفظ فوقه.

## 5. مقطع الأنيميشن (Clip)

```json
{
  "id": "01J...",
  "name": "Walk",
  "fps": { "num": 30, "den": 1 },
  "duration": 705600000,
  "skeleton": "rig:01J...",
  "loop": { "enabled": true, "start": 0, "end": 705600000 },
  "rootMotion": { "mode": "extract", "bone": "root", "axes": "xz+yaw" },
  "tracks": [
    {
      "id": "01J...",
      "target": { "kind": "bone", "name": "thigh_l" },
      "channel": "rotation",
      "keys": [
        { "t": 0, "v": [0, 0, 0, 1], "interp": "cubic",
          "inTan": [0,0,0,0], "outTan": [0,0,0,0] }
      ],
      "locked": false, "group": "Legs"
    }
  ],
  "events": [ { "t": 352800000, "name": "foot_contact_l", "data": {} } ],
  "constraints": [],
  "metadata": { "source": "manual" }
}
```

- `channel`: `translation` (‏3 قيم، متر) أو `rotation` (‏4 قيم، Quaternion xyzw) أو `scale` (‏3 قيم) أو `custom:<name>` (قيمة واحدة).
- `interp`: `step` أو `linear` أو `cubic`. الدوران بـ`linear` يُحسب بـSlerp، وبـ`cubic` بـSquad مع ظلال (tangents) قابلة للتحرير.
- `t` بوحدة flick (انظر ARCHITECTURE.md §5). الواجهة تعرضه كإطار: `frame = t * fps / 705600000`.
- لا تخزين لمفاتيح مخبوزة (baked) إلا عند الطلب. المقاطع المولّدة بالـAI تُحوَّل إلى مفاتيح مُختزلة قابلة للتحرير، مع حفظ الأصل الخام في `metadata/ai/`.

## 6. الهيكل العظمي (Rig)

```json
{
  "id": "01J...", "name": "Mannequin",
  "bones": [ { "name": "pelvis", "parent": "root",
               "rest": { "t": [0,0.95,0], "r": [0,0,0,1], "s": [1,1,1] },
               "mass": 11.2, "limits": null } ],
  "humanoidMap": { "pelvis": "pelvis", "spine_01": "spine1" },
  "ik": [ { "name": "leg_l", "chain": ["thigh_l","calf_l"], "target": "ik_foot_l", "pole": "pole_knee_l" } ]
}
```

`humanoidMap` يربط العظام بأسماء الهيكل القياسي لـBlazma، ويُستخدم في Retargeting وفي تحويل مخرجات Kimodo.
