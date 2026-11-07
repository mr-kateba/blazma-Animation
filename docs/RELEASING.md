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
scripts/build-release.sh 0.9.0 --from-git
scripts/release-smoke.sh 0.9.0
```

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
