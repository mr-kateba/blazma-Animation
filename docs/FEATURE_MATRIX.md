# مصفوفة الميزات

الحالة: ✅ = منفّذ ومُختبَر في النواة، 🖥️ = له واجهة، بدون علامة = مخطط. Simple = تظهر في الوضع البسيط، Adv = في الوضع المتقدم فقط (الميزة موجودة في الوضعين، والإخفاء للواجهة فقط).

| الميزة | المرحلة | الوحدة | الواجهة | CLI | MCP (اسم الأداة) |
|---|---|---|---|---|---|
| ✅🖥️ إنشاء/فتح/حفظ مشروع | 1 | Core | Simple | ✓ | project_create / project_open / project_save |
| ✅🖥️ Undo / Redo | 1 | Commands | Simple | ✓ (`exec --undo`) | edit_undo / edit_redo |
| ✅🖥️ Autosave + استعادة | 1 | Core | Simple | — | — |
| ✅🖥️ ثيم داكن/فاتح | 1 | UI | Simple | — | — |
| ✅🖥️ العربية + RTL | 1 | UI | Simple | — | — |
| ✅🖥️ فحص المشهد | 1 | Core | Simple | ✓ | scene_inspect |
| ✅ Mannequin مدمج | 2 | Rigging | Simple | ✓ | — (يُنشأ مع المشروع) |
| ✅ استيراد glTF/GLB | 2 | Rigging/Export | Simple | ✓ | rig_import |
| ✅ فحص الهيكل وتقرير التوافق | 2 | Rigging | Adv | ✓ | rig_inspect |
| ✅ Rig mapping (تلقائي + يدوي) | 2 | Rigging | Adv | ✓ | rig_map |
| ✅ FK + Gizmos | 2 | Rigging/UI | Simple | — | pose_get / pose_set |
| ✅ Two-bone IK + Pole + Pinning | 2 | Rigging | Simple | ✓ | pose_set (مع أهداف IK) |
| ✅ حدود المفاصل | 2 | Rigging | Adv | ✓ | rig_map |
| ✅ Mirror / Copy / Paste pose | 2 | Rigging | Simple | ✓ | pose_mirror |
| ✅ مكتبة الوضعيات | 2 | Core | Simple | ✓ | pose_library_* |
| Timeline + مفاتيح + تشغيل | 3 | Core/UI | Simple | ✓ | keyframe_add / keyframe_remove / keyframe_move |
| Interpolation (Step/Linear/Cubic) | 3 | Motion | Simple | ✓ | animation_edit |
| Dope Sheet | 3 | UI | Simple | — | — |
| Graph Editor (تحرير فعلي) | 3 | Motion/UI | Adv | ✓ | curve_edit |
| Motion trails / Onion skin | 3 | Motion/UI | Simple | — | animation_preview |
| Markers / Loop range | 3 | Core | Simple | ✓ | animation_edit |
| تصدير GLB | 4 | Export | Simple | ✓ | animation_export |
| JSON sidecar + Events | 4 | Export | Adv | ✓ | animation_export |
| Root motion | 4 | Motion | Adv | ✓ | animation_edit |
| تصدير Godot | 4 | Export | Simple | ✓ | animation_export |
| التحقق من المقطع | 4 | Motion | Simple | ✓ | animation_validate |
| MCP server + CLI + صلاحيات | 5 | Agent | Adv | — | — |
| AI Workspace + مسودات + Approve | 5 | UI/AI | Simple | — | draft_apply / draft_reject |
| ✅ مركز الكتلة + Support polygon + التوازن (COM/ZMP/Capture point) | 6 | Physics | Simple | ✓ | physics_analyze / physics_com |
| ✅ Auto-balance | 6 | Physics | Adv | ✓ | physics_auto_balance |
| ✅ مسارات COM/الورك/القدمين | 6 | Physics | Adv | ✓ | physics_trajectories |
| مسارات باليستية / قفز (توقع مسار الطيران) | 6+ | Physics | Adv | ✓ | — |
| ✅ الحركة الثانوية (Spring) | 6 | Physics | Adv | ✓ | physics_secondary_motion |
| ✅ توليد حركة من نص (Kimodo اختياري + مزوّد إجرائي للاختبار) كـJob في الخلفية | 7 | AI | Simple | ✓ | motion_generate / motion_job_status / motion_job_cancel / motion_job_apply |
| ✅ تشخيص المزوّدات والعتاد (دون شبكة) | 7 | AI | Adv | ✓ | motion_providers |
| ✅ مسودات المقاطع المولّدة (قبول/رفض) | 7 | AI/Commands | Simple | ✓ | clip_accept_draft / clip_delete |
| ✅ Retarget من SOMA إلى أي هيكل بشري مطابَق (داخل motion_job_apply) | 7 | AI | — | ✓ | motion_job_apply |
| ✅ Retarget بين الشخصيات (rig ← rig، نسب مختلفة، أهداف IK، تثبيت القدمين) | 8 | Rigging/Motion | Adv | ✓ | clip_retarget |
| ✅ Blending / انتقالات (تسلسل مقاطع، محاذاة الجذر، مطابقة طور القدمين، خبز) | 8 | Motion | Adv | ✓ | sequence_create / sequence_add_clip / sequence_set_blend / sequence_update_item / sequence_update / sequence_remove / sequence_inspect / sequence_evaluate / sequence_bake |
| Retiming / Loop cleanup | 8+ | Motion | Adv | ✓ | — |
| ✅ كشف التلامس / Foot locking | 8 | Physics/Motion | Simple | ✓ | clip_contacts / clip_foot_lock |
| ✅ أنيميشن الأجسام (Props، ربط بعظمة، تصدير GLB) | 8 | Core/Motion/Export | Simple | ✓ | prop_add / prop_import / prop_remove / prop_attach / prop_detach / prop_set_transform / prop_evaluate / key_set {props} |
| ✅ قوالب المقاطع (مدمجة + مكتبة المشروع) | 8 | AI/Motion | Simple | ✓ | template_list / template_apply / template_save_clip / template_delete |
| أحداث القتال + Hitbox/Hurtbox | 9 | Combat | Adv | ✓ | combat_* |
| تصدير Unity / Unreal / FBX | 10+ | Export | Adv | ✓ | animation_export |
| حزم Windows/Linux + Installer | 10 | Packaging | — | — | — |

ملاحظة: FBX لن يُعلن دعمه قبل اختباره على ملفات حقيقية. خيار التنفيذ المرجّح هو الاعتماد على تحويل خارجي (FBX2glTF أو ufbx) لأن مواصفات FBX مغلقة. يحتاج ذلك إلى قرار لاحق.
