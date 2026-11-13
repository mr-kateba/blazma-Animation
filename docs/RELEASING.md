# إصدار نسخة جديدة (بناء محلي)

كل شيء يُبنى بأمر واحد على جهازك (Linux أو WSL أو حاوية). لا حاجة لـ GitHub Actions.

## المتطلبات (مرة واحدة)

- .NET SDK 8 و10، وأداة `zip` و`file`، و`nsis` و`desktop-file-utils` (لبناء AppImage) (`sudo apt-get install -y nsis zip file desktop-file-utils p7zip-full`).
- Godot 4.7.2 **.NET** لنظام Linux في `/opt/godot/Godot_v4.7.2-stable_mono_linux_x86_64/` (أو متغير `GODOT=...`).
- قوالب التصدير الرسمية في `~/.local/share/godot/export_templates/4.7.2.stable.mono/`: ملفات `windows_*x86_64*` و`linux_*x86_64*` من
  `Godot_v4.7.2-stable_mono_export_templates.tpz` (من https://github.com/godotengine/godot/releases/tag/4.7.2-stable، وتحقق من `SHA512-SUMS.txt`).
- لصنع AppImage يُحمَّل `appimagetool` **الإصدار 1.9.0** (مثبَّت في السكربت مع SHA-256) مرة واحدة إلى `build/cache`؛ عدم تطابق البصمة يوقف البناء، وتعذّر التنزيل يتخطى AppImage بتحذير. `APPIMAGETOOL=...` لاستعمال نسخة محلية.
- `7z` (‏`p7zip-full`) اختياري: به يتحقق `release-smoke.sh` من محتوى المثبّت (لا `portable.txt`، وجود `tools/kimodo` إن ضُمِّن الـrunner)؛ بدونه يُتخطى هذا الفحص مع ملاحظة.

## البناء

```
scripts/build-release.sh 1.0.0 --from-git
scripts/release-smoke.sh 1.0.0
```

**رقم الإصدار يُمرَّر هنا فقط**: السكربت يكتبه في المثبّت (`VERSION`/`NUMVER` لـ NSIS) و`VERSION.txt` وأسماء الملفات و`cuda-backend.json`، فلا تعدّله في الكود. قبل البناء أضف قسم الإصدار إلى `CHANGELOG.md` وحدّث رأس `docs/STATUS.md` والإصدار المذكور في `README.md` و`docs/GUIDE_AR.md` و`docs/GUIDE_EN.md`.

الخيارات: `--from-git[=REV]` يبني نسخة نظيفة من commit (موصى به للإصدار الرسمي كي لا تتسرب تعديلات غير مكتملة؛ التوثيق والتراخيص وملاحظات الإصدار تؤخذ أيضًا من تلك النسخة)، `--skip-tests`، `--skip-linux`، `--skip-appimage`، `--skip-installer`.
`release-smoke.sh --full` يشغّل أيضًا اختبار الواجهة الكامل على نسخة Linux المصدّرة (~4 دقائق).

ما يفعله البناء بالترتيب: اختبارات النواة (`-warnaserror`، أي فشل يوقف الإصدار) ← تصدير Godot لـ Windows وLinux ← نشر أدوات الوكلاء
(`blazma-mcp`, `blazma-anim`) ملفًا واحدًا مستقلًا لـ win-x64 وlinux-x64 ← التجميع ← ملاحظات الإصدار من `docs/reports` ← `SHA256SUMS.txt`.

## المخرجات: `build/release/<الإصدار>/`

| الملف | الاستخدام |
|---|---|
| `BlazmaAnimation-<v>-setup.exe` | مثبّت Windows ‏64 بت للمستخدم الحالي (بدون مدير): قائمة Start، اختصار سطح مكتب اختياري، إزالة، ربط `.blazanim` في HKCU. يطلب إغلاق البرنامج وأدواته إن كانت تعمل (mutex ‏`BlazmaAnimation-Running` وأسماء العمليات) قبل التثبيت والإزالة. الإزالة تحذف بالضبط الملفات والمجلدات التي ثبّتها (قائمة يولدها السكربت)، وتحذف من الربط قيمتنا فقط (تبقى `OpenWithProgids` للبرامج الأخرى)، وتعرض (الافتراضي: لا) حذف النماذج المنزّلة في `%LOCALAPPDATA%\BlazmaAnimation\models` مع حجمها |
| `BlazmaAnimation-<v>-win64.zip` | نسخة محمولة (فيها `portable.txt`) |
| `BlazmaAnimation-<v>-linux-x64.tar.gz` | Linux محمولة؛ فيها `blazma-animation.desktop` (‏Exec=`BlazmaAnimation.x86_64`) و`blazma-animation-mime.xml` لنوع `application/x-blazanim` (‏`xdg-mime install blazma-animation-mime.xml`) |
| `BlazmaAnimation-<v>-x86_64.AppImage` | Linux ملف واحد |
| `kimodo-runner-<v>-win64.zip` / `kimodo-runner-<v>-linux-x64.tar.gz` | الـrunner وحده (مع بناء runner فقط)؛ نسخة Windows بلا CUDA |
| `blazma-cuda-backend-<v>-win-x64.zip` | **واجهة CUDA الاختيارية (D47)**: `ggml-cuda.dll` و`cudart64_12`/`cublas64_12`/`cublasLt64_12` و`NVIDIA_CUDA_NOTICE.txt` و`SHA256SUMS-cuda-backend.txt` (نحو 550 MB). لا تدخل المثبّت ولا الـzip؛ البرنامج ينزّلها من رابط الإصدار حين يضغط المستخدم الزر في الإعدادات ← AI، ويتحقق منها بالقيم في `tools/kimodo/cuda-backend.json`. **يجب رفعها مع الإصدار نفسه** (الوسم `v<v>`) وإلا يفشل الزر بخطأ HTTP |
| `SHA256SUMS.txt` | بصمات كل الملفات |

كل حزمة فيها `LICENSE.txt` (Blazma: ملكية خاصة، كل الحقوق محفوظة لـ mr-kateba) و`THIRD_PARTY_NOTICES.md` ومجلد `legal/` بالنصوص الكاملة: رخصة Godot وCOPYRIGHT.txt الخاص به، رخصة .NET وإشعارات الطرف الثالث لـ .NET 8 و10، Apache-2.0، رخصة MCP C# SDK، رخصة وNOTICE لـ kimodo.cpp، رخصة NVIDIA Kimodo (بيانات هيكل SOMA)، GGML وnlohmann/json (MIT)، وخطوط IBM Plex (OFL).
| `RELEASE_NOTES-<v>.md` | ملاحظات الإصدار (تُلصق في وصف الـ Release) |

## المثبّت أم المحمولة؟

- **المثبّت:** للمستخدم العادي؛ اختصارات وإزالة نظيفة وربط الملفات. الإعدادات في `%APPDATA%\BlazmaAnimation`.
- **المحمولة:** فك الضغط وشغّل؛ وجود `portable.txt` بجانب البرنامج يجعل الإعدادات والحفظ التلقائي في `userdata` بجانبه.
  (يتطلب تعديلًا صغيرًا في الكود؛ راجع تقرير المرحلة 10.)
- البرنامج غير موقَّع رقميًا: يظهر تحذير SmartScreen (More info ← Run anyway).

## الرفع يدويًا إلى GitHub Releases (من المتصفح)

1. افتح المستودع ← **Releases** ← **Draft a new release**.
2. أنشئ Tag جديدًا مثل `v0.9.0` (Target: الفرع/الـ commit الذي بنيت منه).
3. العنوان: `Blazma Animation 0.9.0`. الصق محتوى `RELEASE_NOTES-0.9.0.md` في الوصف.
4. اسحب كل الملفات من `build/release/0.9.0/` إلى مربع المرفقات (بما فيها `SHA256SUMS.txt`).
5. للنسخ التجريبية فعّل **Set as a pre-release**، ثم **Publish release**.

## بديل: عبر GitHub Actions أو سطر الأوامر

- `gh release create v0.9.0 build/release/0.9.0/* --title "Blazma Animation 0.9.0" --notes-file build/release/0.9.0/RELEASE_NOTES-0.9.0.md --prerelease`
- أو شغّل workflow **Release** يدويًا (Actions ← Release ← Run workflow) مع الإصدار؛ يستدعي نفس `scripts/build-release.sh` ويرفع المخرجات
  كـ artifact، ويُنشئ الـ Release إذا فعّلت `publish`. (يستهلك دقائق Actions؛ الطريقة المحلية مجانية.)

## وقت تشغيل VC++ في المثبّت

عند وجود Windows kimodo-runner ينزّل `build-release.sh` الملف `vc_redist.x64.exe` مرة واحدة إلى `build/cache` من `https://aka.ms/vs/17/release/vc_redist.x64.exe` ويتحقق من توقيع Microsoft بـ `osslsigncode verify` (`apt-get install -y osslsigncode`)؛ بدونه يطبع تحذيرًا مع SHA-256 للتوثيق اليدوي. يُضمَّن في المثبّت فقط (لا في مجلد التطبيق ولا في الـzip؛ الـzip فيه `README-VC_REDIST.txt`)، ويفحص `release-smoke.sh` وجوده عبر `7z l`. الإشعار: `packaging/legal/MSVC_REDIST_NOTICE.txt`.

## واجهة CUDA الاختيارية (D47)

عند وجود `ggml-cuda.dll` في بناء الـrunner (artifact ‏`kimodo-runner-windows-x64-cuda`، مثلًا من التشغيل 42365146397) ينقل `build-release.sh`
ملفات CUDA من `tools/kimodo` إلى `blazma-cuda-backend-<v>-win-x64.zip`، ويحذف أسماءها من `SHA256SUMS*.txt` الباقية ويعيد التحقق منها،
ويكتب `tools/kimodo/cuda-backend.json` (الرابط `https://github.com/<repo>/releases/download/v<v>/<asset>`، الحجم، SHA-256 للأرشيف ولكل ملف؛
المستودع من `BLAZMA_RELEASE_REPO`، الافتراضي `mr-kateba/blazma-Animation`). ملاحظات الإصدار تذكر الملف وحجمه وبصمته.
`release-smoke.sh` يتحقق: لا ملفات CUDA في الـzip ولا المثبّت ولا `kimodo-runner-*.zip`، المثبّت أصغر من 250 MB، الأرشيف سليم وفيه كل
الملفات، `cuda-backend.json` يطابق الأرشيف وكل ملف، والأرشيف مذكور في `SHA256SUMS.txt` وبصمته في ملاحظات الإصدار.
بدون بناء CUDA لا يُنتج الأرشيف ولا `cuda-backend.json`، ولا يظهر الزر.

الأحجام المتوقعة (من ملفات التشغيل 42365146397): `ggml-cuda.dll` ‏369 MB، `cublasLt64_12.dll` ‏474 MB، `cublas64_12.dll` ‏100 MB،
`cudart64_12.dll` ‏0.5 MB (نحو 943 MB غير مضغوطة، نحو 549 MB بضغط zip). الباقي في `tools/kimodo` نحو 60 MB (نحو 19 MB مضغوطة).
