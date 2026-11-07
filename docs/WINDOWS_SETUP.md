# دليل التشغيل على Windows

هذا الدليل لتشغيل مشروع Blazma Animation على Windows 10 أو Windows 11 (x64).
اتبع الخطوات بالترتيب.

## 1. ما الذي تحتاجه

1. **Git for Windows**
   - حمّله من: https://git-scm.com/download/win
   - ثبّته بالإعدادات الافتراضية.

2. **.NET SDK 8**
   - مطلوب لأن Godot .NET يعتمد عليه.
   - حمّله من: https://dotnet.microsoft.com/download

3. **.NET SDK 10**
   - مطلوب لتشغيل الاختبارات فقط.
   - حمّله من نفس الرابط: https://dotnet.microsoft.com/download

4. **Godot 4.7.2 نسخة .NET**
   - مهم: حمّل نسخة **.NET** وليس النسخة العادية.
   - صفحة التحميل: https://godotengine.org/download/archive/4.7.2-stable/
   - اسم الملف يشبه `Godot_v4.7.2-stable_mono_win64.zip`.
   - فك الضغط في مجلد مثل `C:\Tools\Godot`.

## 2. تحميل الكود

افتح PowerShell ونفّذ الأوامر التالية:

1. استنسخ المستودع:
   ```
   git clone https://github.com/mr-kateba/blazma-animation.git
   ```
2. ادخل إلى المجلد:
   ```
   cd blazma-animation
   ```
3. انتقل إلى الفرع الصحيح:
   ```
   git checkout claude/stoic-ritchie-2nyfnl
   ```

## 3. طريقة التشغيل

### الطريقة A (الأسهل): سكربت التشغيل

من داخل مجلد المشروع نفّذ:

```
powershell -ExecutionPolicy Bypass -File scripts\run-windows.ps1
```

ماذا يفعل السكربت:

1. يتأكد أن .NET SDK 8 مثبّت.
2. يبحث عن Godot تلقائياً.
3. يبني المشروع (`Blazma.UI`).
4. يشغّل Godot مرة واحدة لاستيراد الملفات.
5. يشغّل التطبيق.

خيارات إضافية:

- `-GodotPath "C:\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe"`
  استخدمه إذا لم يجد السكربت Godot. يمكنك أيضاً تمرير مجلد يحتوي على الملف.
- `-Editor`
  يفتح محرر Godot بدلاً من تشغيل التطبيق مباشرة.
- `-Test`
  يشغّل الاختبارات فقط ثم يتوقف.

مثال:

```
powershell -ExecutionPolicy Bypass -File scripts\run-windows.ps1 -GodotPath "C:\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe"
```

### الطريقة B: من داخل Godot يدوياً

1. افتح Godot 4.7.2 .NET.
2. اضغط **Import**.
3. اختر الملف `app/godot/project.godot`.
4. اضغط زر **Build** (أعلى الشاشة).
5. اضغط **Run** أو اضغط المفتاح **F5**.

## 4. تشغيل الاختبارات

من مجلد المشروع نفّذ:

```
dotnet test Blazma.slnx
```

## 5. مكان حفظ البيانات

يحفظ التطبيق الإعدادات واستعادة الحفظ التلقائي في:

```
%APPDATA%\BlazmaAnimation
```

## 6. حل المشاكل

### لم يتم العثور على .NET SDK

- تأكد أنك ثبّتّ .NET SDK 8.
- أغلق PowerShell وافتحه من جديد بعد التثبيت.
- تحقق بتنفيذ: `dotnet --list-sdks`
  يجب أن ترى سطراً يبدأ بـ `8.`.

### Godot فُتح بدون دعم C#

- السبب: حمّلت النسخة العادية وليس نسخة **.NET**.
- الحل: حمّل النسخة التي اسمها يحتوي على `mono` من صفحة التحميل المذكورة في الخطوة 1.

### Build failed: restore needs internet access to nuget.org

- عند أول بناء يحتاج النظام إلى تحميل الحزم من https://nuget.org.
- تأكد أن الإنترنت يعمل، ثم أعد المحاولة.
- إذا كنت خلف جدار حماية أو وكيل (proxy)، اطلب المساعدة من المسؤول عن الشبكة.

### تحذير Windows SmartScreen عند تشغيل Godot

- قد يظهر Windows تحذيراً لأن البرنامج جديد لدى Windows.
- اضغط **More info** ثم **Run anyway**.
- حمّل Godot فقط من الرابط الرسمي المذكور أعلاه.

## 7. النسخة الجاهزة (بدون تثبيت أي شيء)

تُنشر النسخ الجاهزة في صفحة **Releases** في GitHub. اختر واحدة من اثنتين:

### أ) المثبّت (موصى به): `BlazmaAnimation-<الإصدار>-setup.exe`

1. شغّل الملف. **لا يحتاج صلاحيات مدير**؛ يُثبَّت للمستخدم الحالي في `%LOCALAPPDATA%\Programs\BlazmaAnimation`.
2. اختر المكوّنات: البرنامج (إلزامي)، ربط ملفات `.blazanim`، واختياريًا اختصار سطح المكتب. يُضاف اختصار في قائمة Start.
3. للإزالة: Settings ← Apps، أو `uninstall.exe` في مجلد التثبيت. إن كان البرنامج أو أدواته (`blazma-mcp`/`blazma-anim`) يعمل، يطلب المثبّت والمزيل إغلاقه أولًا (إعادة المحاولة/إلغاء). المثبّت يعمل على Windows ‏64 بت فقط.
4. الإعدادات تبقى في `%APPDATA%\BlazmaAnimation` ولا تُحذف عند الإزالة. نماذج الذكاء الاصطناعي المنزّلة (`%LOCALAPPDATA%\BlazmaAnimation\models`، قد تكون عدة GB) يسألك المزيل عنها ويعرض حجمها، والافتراضي **الإبقاء** عليها.

### ب) المحمولة: `BlazmaAnimation-<الإصدار>-win64.zip`

1. فك الضغط في أي مجلد (يمكن وضعه على فلاش).
2. شغّل `BlazmaAnimation.exe`. لا تحتاج تثبيت .NET ولا Godot.
3. الملف `portable.txt` بجانب البرنامج يعني الوضع المحمول: تُحفظ الإعدادات والحفظ التلقائي في مجلد `userdata` بجانب البرنامج بدل `%APPDATA%`. احذف الملف لإلغاء الوضع المحمول.

### تحذير SmartScreen

البرنامج غير موقَّع رقميًا بعد. إذا ظهر التحذير اضغط **More info** ثم **Run anyway**. يمكنك التحقق من الملف بمقارنة `SHA256SUMS.txt`:

```
certutil -hashfile BlazmaAnimation-<الإصدار>-setup.exe SHA256
```

أدوات الذكاء الاصطناعي (`blazma-mcp.exe` و`blazma-anim.exe`) داخل مجلد `tools` في كلا النسختين. بناء الإصدارات بنفسك: `docs/RELEASING.md`.

## وقت تشغيل Visual C++ (لتوليد الحركة Kimodo)

`kimodo-runner.exe` مبني بـ MSVC ويحتاج Microsoft Visual C++ 2015–2022 x64 Runtime (الملفات MSVCP140.dll وVCRUNTIME140.dll وVCRUNTIME140_1.dll). الخطأ «MSVCP140.dll was not found» يعني غيابه.

- **المثبّت:** يتحقق من وجوده (الإصدار 14.40 فأعلى) ويثبّت `vc_redist.x64.exe` الرسمي المضمَّن بصمت إن لم يكن موجودًا؛ تُطلب موافقة المدير (UAC) لهذه الخطوة فقط، وبقية التثبيت للمستخدم الحالي. عند الرفض يكتمل التثبيت ويعمل البرنامج، لكن توليد الحركة بالذكاء الاصطناعي يحتاج الـruntime.
- **النسخة المحمولة:** ثبّته يدويًا مرة واحدة (يحتاج صلاحيات مدير): <https://aka.ms/vs/17/release/vc_redist.x64.exe> (التعليمات أيضًا في `README-VC_REDIST.txt` داخل الـzip).
