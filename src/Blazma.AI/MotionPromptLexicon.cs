namespace Blazma.AI;

// The motion planner's dictionary: English and Arabic (MSA + Gulf, Saudi/Najdi, Hijazi, Kuwaiti, Emirati, Omani, Iraqi, Levantine,
// Egyptian, Sudanese, Maghrebi) verbs, nouns/masdars, directions, speed, manner, body parts, counts, durations, connectors and
// function words. Words are written naturally; every form is normalised on registration (see Normalize), so hamza, ة/ه, ى/ي and
// diacritics never matter. AR() also derives imperative / feminine / "we" forms from the "يفعل" form.
public static partial class MotionPromptPlanner
{
    static void LoadEnglishLexicon()
    {
        A("walk", "walk", "walks", "walking", "walked", "stroll", "strolls", "strolling", "strolled", "march", "marches", "marching", "wander", "wanders", "wandering", "pace", "paces", "pacing", "advance", "advances", "advancing", "approach", "approaches", "approaching");
        A("run", "run", "runs", "running", "ran", "jog", "jogs", "jogging", "jogged", "run away", "runs away", "running away", "flee", "flees", "fleeing", "fled", "race", "races", "racing", "bolt", "bolts", "bolting", "chase", "chases", "chasing");
        A("sprint", "sprint", "sprints", "sprinting", "sprinted", "dash", "dashes", "dashing", "dashed", "rush", "rushes", "rushing", "rushed");
        A("jump", "jump", "jumps", "jumping", "jumped", "hop", "hops", "hopping", "hopped", "leap", "leaps", "leaping", "leaped", "leapt", "bounce", "bounces", "bouncing", "vault", "vaults");
        A("fall", "fall", "falls", "falling", "fell", "collapse", "collapses", "collapsing", "collapsed", "fall down", "falls down", "trip", "trips", "tripping", "tripped", "slip", "slips", "slipping", "slipped", "faint", "faints", "fainting", "fainted", "topple", "topples");
        A("die", "die", "dies", "dying", "died", "dead", "drop dead", "drops dead", "gets killed", "get killed", "is killed", "get shot dead", "gets shot dead");
        A("turn_around", "turn", "turns", "turning", "turned", "turn around", "turns around", "turning around", "turned around", "turn round",
            "spin around", "spins around", "spin", "spins", "spinning", "turn back", "turns back", "pivot", "pivots", "about face");
        A("look_back", "look back", "looks back", "looking back", "looked back", "look behind", "looks behind", "glance back", "glances back",
            "look over shoulder", "looks over shoulder", "look over his shoulder", "looks over his shoulder", "looks over her shoulder", "look over their shoulder", "looks over their shoulder");
        A("look", "look", "looks", "looking", "looked", "glance", "glances", "glancing", "gaze", "gazes", "gazing", "stare", "stares", "staring", "peek", "peeks", "peeking");
        A("look_around", "look around", "looks around", "looking around", "looked around", "look about", "looks about", "glance around", "glances around", "glancing around",
            "scan the area", "scans the area", "scanning the area", "survey", "surveys", "surveying", "search around", "searches around");
        A("crouch", "crouch", "crouches", "crouching", "crouched", "squat", "squats", "squatting", "squatted", "duck", "ducks", "ducking", "ducked", "bend down", "bends down", "hide", "hides", "hiding", "hid", "take cover", "takes cover");
        A("kneel", "kneel", "kneels", "kneeling", "knelt", "kneel down", "kneels down", "go down on one knee", "goes down on one knee");
        A("sit", "sit", "sits", "sitting", "sat", "sit down", "sits down", "sitting down", "sat down", "take a seat", "takes a seat", "be seated");
        A("stand_up", "stand up", "stands up", "standing up", "stood up", "get up", "gets up", "getting up", "got up", "rise", "rises", "rising", "rose", "wake up", "wakes up", "get back up", "gets back up");
        A("lie_down", "lie down", "lies down", "lying down", "lay down", "lie flat", "lies flat", "lie on the ground", "lies on the ground", "lie on the floor", "lies on the floor", "lay on the ground", "lay on the floor", "lie", "lies", "lying");
        A("sleep", "sleep", "sleeps", "sleeping", "slept", "doze", "dozes", "dozing", "nap", "naps", "napping", "fall asleep", "falls asleep", "go to sleep", "goes to sleep");
        A("crawl", "crawl", "crawls", "crawling", "crawled", "creep forward", "creeps forward", "army crawl");
        A("kick", "kick", "kicks", "kicking", "kicked", "stomp", "stomps", "stomping");
        A("punch", "punch", "punches", "punching", "punched", "hit", "hits", "hitting", "strike", "strikes", "striking", "struck", "attack", "attacks", "attacking", "attacked",
            "swing", "swings", "swinging", "box", "boxes", "boxing", "jab", "jabs", "jabbing", "fight", "fights", "fighting", "throw a punch", "throws a punch", "throw punches", "throws punches");
        A("slap", "slap", "slaps", "slapping", "slapped", "smack", "smacks");
        A("slash", "slash", "slashes", "slashing", "slashed", "swing a sword", "swings a sword", "swing his sword", "swings his sword", "swing sword", "swing the sword", "swings the sword", "sword swing", "chop", "chops", "chopping");
        A("stab", "stab", "stabs", "stabbing", "stabbed", "thrust", "thrusts", "thrusting");
        A("headbutt", "headbutt", "headbutts", "headbutting", "headbutted");
        A("wave", "wave", "waves", "waving", "waved", "greet", "greets", "greeting", "say hi", "says hi", "say hello", "says hello", "wave hello", "wave goodbye", "waves goodbye");
        A("point", "point", "points", "pointing", "pointed", "point at", "points at", "point to", "points to", "gesture", "gestures", "gesturing");
        A("salute", "salute", "salutes", "saluting", "saluted", "military salute");
        A("bow", "bow", "bows", "bowing", "bowed", "take a bow", "takes a bow");
        A("clap", "clap", "claps", "clapping", "clapped", "applaud", "applauds", "applauding", "applauded");
        A("dance", "dance", "dances", "dancing", "danced", "groove", "grooves");
        A("idle", "idle", "idles", "idling", "stand", "stands", "standing", "stood", "wait", "waits", "waiting", "waited", "stand still",
            "stands still", "standing still", "breathe", "breathes", "breathing", "rest", "rests", "resting",
            "return to ready stance", "returns to ready stance", "return to a ready stance", "returns to a ready stance", "return to guard stance", "returns to guard stance",
            "return to a guard stance", "returns to a guard stance", "return to guard", "returns to guard", "ready stance", "guard stance", "get ready", "gets ready",
            "return to stance", "returns to stance", "return to idle", "returns to idle", "relax", "relaxes", "relaxing");
        A("stop", "stop", "stops", "stopping", "stopped", "halt", "halts", "freeze", "freezes", "froze", "pause", "pauses", "pausing", "paused");
        A("step_back", "step back", "steps back", "stepping back", "stepped back", "step backward", "steps backward", "step backwards", "steps backwards",
            "back away", "backs away", "backing away", "back up", "backs up", "retreat", "retreats", "retreating", "retreated", "withdraw", "withdraws", "recoil", "recoils");
        A("dodge", "dodge", "dodges", "dodging", "dodged", "evade", "evades", "evading", "sidestep", "sidesteps", "sidestepping", "avoid", "dodge the attack", "dodges the attack");
        A("roll", "roll", "rolls", "rolling", "rolled", "tumble", "tumbles", "somersault", "somersaults", "cartwheel");
        A("climb", "climb", "climbs", "climbing", "climbed", "scale", "scales", "scaling", "ascend", "ascends");
        A("swim", "swim", "swims", "swimming", "swam", "paddle", "paddles");
        A("throw", "throw", "throws", "throwing", "threw", "toss", "tosses", "tossing", "tossed", "hurl", "hurls", "hurling", "fling", "flings", "pitch", "pitches");
        A("catch", "catch", "catches", "catching", "caught", "intercept", "intercepts");
        A("pick_up", "pick up", "picks up", "picking up", "picked up", "pick it up", "picks it up", "grab", "grabs", "grabbing", "grabbed", "lift", "lifts", "lifting", "lifted", "take", "takes", "taking");
        A("carry", "carry", "carries", "carrying", "carried", "haul", "hauls", "hauling");
        A("push", "push", "pushes", "pushing", "pushed", "shove", "shoves", "shoving", "shoved");
        A("pull", "pull", "pulls", "pulling", "pulled", "drag", "drags", "dragging", "dragged", "tug", "tugs", "tugging", "yank", "yanks");
        A("block", "block", "blocks", "blocking", "blocked", "parry", "parries", "parrying", "defend", "defends", "defending", "guard", "guards", "guarding", "shield", "shields", "shielding");
        A("cheer", "cheer", "cheers", "cheering", "cheered", "hooray", "rejoice", "rejoices");
        A("celebrate", "celebrate", "celebrates", "celebrating", "celebrated", "victory pose", "victory dance");
        A("cry", "cry", "cries", "crying", "cried", "sob", "sobs", "sobbing", "weep", "weeps", "weeping", "tear up", "tears up");
        A("laugh", "laugh", "laughs", "laughing", "laughed", "giggle", "giggles", "giggling", "chuckle", "chuckles", "chuckling");
        A("smile", "smile", "smiles", "smiling", "smiled", "grin", "grins", "grinning");
        A("think", "think", "thinks", "thinking", "thought", "ponder", "ponders", "pondering", "contemplate", "contemplates", "scratch head", "scratches head", "scratches his head", "scratch their head");
        A("talk", "talk", "talks", "talking", "talked", "speak", "speaks", "speaking", "spoke", "say", "says", "saying", "said", "chat", "chats", "chatting", "explain", "explains", "explaining", "converse", "conversing");
        A("phone_call", "talk on the phone", "talks on the phone", "talking on the phone", "phone call", "make a phone call", "makes a phone call", "making a phone call", "call", "calls", "calling", "phone", "phones", "phoning",
            "answer the phone", "answers the phone", "answering the phone", "on the phone", "speak on the phone", "speaks on the phone", "speaking on the phone", "talk on phone", "talks on phone");
        A("shout", "shout", "shouts", "shouting", "shouted", "yell", "yells", "yelling", "yelled", "scream", "screams", "screaming", "screamed", "call out", "calls out", "calling out", "holler", "hollers");
        A("sing", "sing", "sings", "singing", "sang", "whistle", "whistles", "whistling", "hum", "hums", "humming");
        A("get_hit", "get hit", "gets hit", "getting hit", "got hit", "take a hit", "takes a hit", "taking a hit", "get punched", "gets punched", "gets shot", "get shot", "getting shot", "got shot", "gets kicked", "get kicked",
            "gets slapped", "get slapped", "react", "reacts", "reacting", "reacted", "flinch", "flinches", "flinching", "wince", "winces", "wincing", "stagger back", "staggers back", "take damage", "takes damage", "gets stabbed", "get stabbed");
        A("startle", "startle", "startles", "startling", "get scared", "gets scared", "getting scared", "get startled", "gets startled", "get frightened", "gets frightened", "panic", "panics", "panicking", "panicked", "gasp", "gasps", "gasping",
            "freak out", "freaks out", "freaking out", "get surprised", "gets surprised", "be surprised", "is surprised", "are surprised", "jump scare", "scared of");
        A("tremble", "tremble", "trembles", "trembling", "trembled", "shiver", "shivers", "shivering", "shivered", "shake with fear", "shakes with fear", "shudder", "shudders", "quiver", "quivers");
        A("limp", "limp", "limps", "limping", "limped", "hobble", "hobbles", "hobbling", "hobbled", "walk with a limp", "walks with a limp", "dragging a leg");
        A("stagger", "stagger", "staggers", "staggering", "staggered", "stumble", "stumbles", "stumbling", "stumbled", "totter", "totters", "tottering", "sway", "sways", "swaying", "lurch", "lurches", "lurching", "wobble", "wobbles");
        A("sneak", "sneak", "sneaks", "sneaking", "sneaked", "snuck", "tiptoe", "tiptoes", "tiptoeing", "tiptoed", "creep", "creeps", "creeping", "crept", "sneak up", "sneaks up", "prowl", "prowls", "prowling", "stalk", "stalks", "stalking", "skulk");
        A("fight_stance", "fighting stance", "fight stance", "combat stance", "battle stance", "boxing stance", "takes a fighting stance", "take a fighting stance", "taking a fighting stance", "get into a fighting stance", "gets into a fighting stance",
            "takes a boxing stance", "take a boxing stance", "raise fists", "raises fists", "raises his fists", "raising fists", "guard up", "puts guard up", "puts up fists", "put up fists", "fists up", "combat ready", "battle ready", "ready to fight", "gets ready to fight");
        A("aim", "aim", "aims", "aiming", "aimed", "aim a gun", "aims a gun", "aiming a gun", "aims a weapon", "aim a weapon", "point a gun", "points a gun", "pointing a gun", "aim a rifle", "aims a rifle", "aiming a rifle", "aim a pistol", "aims a pistol", "draw a bow", "draws a bow", "takes aim", "take aim");
        A("shoot", "shoot", "shoots", "shooting", "shot", "fire", "fires", "firing", "fired", "fire a gun", "fires a gun", "firing a gun", "pull the trigger", "pulls the trigger", "pulling the trigger", "open fire", "opens fire", "opening fire");
        A("drink", "drink", "drinks", "drinking", "drank", "sip", "sips", "sipping", "sipped", "gulp", "gulps", "gulping", "drink water", "drinks water", "drinking water", "drink coffee", "drinks coffee", "drinks tea");
        A("eat", "eat", "eats", "eating", "ate", "bite", "bites", "biting", "chew", "chews", "chewing", "munch", "munches", "munching", "swallow", "swallows", "devour", "devours", "dine", "dines");
        A("yawn", "yawn", "yawns", "yawning", "yawned");
        A("stretch", "stretch", "stretches", "stretching", "stretched", "stretch arms", "stretches arms", "stretches his arms", "stretch his arms", "warm up", "warms up", "warming up", "stretch out", "stretches out");
        A("nod", "nod", "nods", "nodding", "nodded", "nod head", "nods head", "nods his head", "nod their head", "agree", "agrees", "agreeing");
        A("shake_head", "shake head", "shakes head", "shaking head", "shake his head", "shakes his head", "shakes her head", "shake their head", "shaking his head", "shake no", "disagree", "disagrees", "refuse", "refuses", "refusing", "decline", "declines");
        A("shrug", "shrug", "shrugs", "shrugging", "shrugged", "shrug shoulders", "shrugs shoulders", "shrugs his shoulders");
        A("hug", "hug", "hugs", "hugging", "hugged", "embrace", "embraces", "embracing", "embraced", "cuddle", "cuddles");

        // ---- English manner, speed, direction, counts, negation, connectors
        W(K.Speed, "fast", "fast", "quickly", "quick", "rapidly", "swiftly", "hurriedly", "speedily", "in a hurry", "at full speed", "full speed", "hastily");
        W(K.Speed, "slow", "slow", "slowly", "gently", "carefully", "leisurely", "calmly", "gradually", "unhurriedly", "at a slow pace", "in slow motion");
        W(K.Dir, "forward", "forward", "forwards", "ahead", "straight", "straight ahead", "toward", "towards");
        W(K.Dir, "backward", "backward", "backwards", "back", "behind");
        W(K.Dir, "left", "left", "to the left", "leftward", "leftwards");
        W(K.Dir, "right", "right", "to the right", "rightward", "rightwards");
        W(K.Dir, "up", "up", "upward", "upwards", "overhead");
        W(K.Dir, "down", "downward", "downwards", "to the ground");
        W(K.Dir, "side", "sideways", "to the side", "aside", "laterally");
        W(K.Dir, "circle", "in a circle", "in circles", "around in a circle", "in a loop");
        W(K.Dir, "in_place", "in place", "on the spot", "on the same spot", "without moving");
        W(K.Manner, "zombie", "zombie", "like a zombie", "zombie-like", "zombielike", "undead", "like the undead");
        W(K.Manner, "old", "old", "elderly", "like an old man", "like an old woman", "like an old person", "like an elderly person", "like an elder", "aged", "frail", "senior");
        W(K.Manner, "tired", "tired", "exhausted", "weary", "wearily", "sleepy", "drained", "worn out", "fatigued", "tiredly", "lethargic", "lethargically");
        W(K.Manner, "drunk", "drunk", "drunken", "drunkenly", "tipsy", "intoxicated", "like a drunk", "like a drunk person", "wasted");
        W(K.Manner, "scared", "scared", "afraid", "frightened", "terrified", "fearful", "fearfully", "in fear", "in terror", "spooked");
        W(K.Manner, "nervous", "nervous", "nervously", "anxious", "anxiously", "worried", "uneasy", "tense", "tensely");
        W(K.Manner, "angry", "angry", "angrily", "furious", "furiously", "mad", "enraged", "irritated", "in anger", "aggressively", "aggressive");
        W(K.Manner, "happy", "happy", "happily", "joyful", "joyfully", "excited", "excitedly", "cheerful", "cheerfully", "delighted", "gleeful", "joyous");
        W(K.Manner, "sad", "sad", "sadly", "depressed", "gloomy", "gloomily", "sorrowful", "heartbroken", "dejected", "miserable");
        W(K.Manner, "proud", "proud", "proudly");
        W(K.Manner, "confident", "confident", "confidently", "boldly", "bold", "with confidence", "swagger", "swaggering");
        W(K.Manner, "careful", "cautiously", "cautious", "warily", "wary", "with caution", "stealthily", "quietly");
        W(K.Manner, "forceful", "forcefully", "powerful", "powerfully", "strong", "strongly", "hard", "with force", "violently", "heavily", "fiercely", "with power");
        W(K.Manner, "sudden", "suddenly", "abruptly", "all of a sudden", "out of nowhere", "unexpectedly", "in a flash");
        W(K.Part, "hand", "hand", "hands", "fist", "fists", "arm", "arms", "palm", "finger");
        W(K.Part, "leg", "leg", "legs", "foot", "feet", "knee", "knees");
        W(K.Part, "head", "head", "forehead");
        W(K.Part, "hand", "hand", "hands", "fist", "fists");
        W(K.Negation, "", "without", "not", "never", "don't", "doesn't", "didn't", "won't", "dont", "doesnt", "no", "nor", "unable to", "refrains from", "instead of");
        Put("twice", new Entry(K.Repeat, "", 2), "twice", false);
        Put("thrice", new Entry(K.Repeat, "", 3), "thrice", false);
        Put("once", new Entry(K.Repeat, "", 1), "once", false);
        W(K.Times, "", "times", "time", "x");
        W(K.StepWord, "", "steps", "step", "strides");
        W(K.Connector, "", "then", "afterwards", "afterward", "next", "finally", "lastly", "later", "and then", "and after that", "after that",
            "after this", "and finally", "and next", "then finally", "at last", "in the end", "at the end", "a moment later", "moments later", "after a while", "after a moment",
            "when", "as soon as", "right after", "right after that", "immediately after", "immediately afterwards", "following that", "and afterwards", "eventually", "after which", "and later", "thereafter", "subsequently");
        W(K.And, "", "and", "&", "plus", "also");
        W(K.Concurrent, "", "while", "whilst", "as he", "as she", "as they", "meanwhile", "at the same time", "simultaneously");
    }


    static void LoadFunctionWords()
    {
        // ---- sequencing connectors (Arabic: MSA, Gulf, Levantine, Iraqi, Egyptian, Sudanese, Maghrebi)
        W(K.Connector, "", "ثم", "وثم", "فثم", "ومن ثم", "من ثم", "بعدين", "وبعدين", "فبعدين", "من بعدين", "ومن بعدين", "بعدها", "وبعدها", "فبعدها", "بعده", "وبعده", "بعد ذلك", "وبعد ذلك",
            "بعد كده", "وبعد كده", "بعد كدا", "وبعد كدا", "بعد كذا", "وبعد كذا", "بعد داك", "وبعد داك", "بعد دا", "وبعد دا", "بعد هيك", "وبعد هيك", "بعد هيج", "وبعد هيج", "بعد هذا", "وبعد هذا",
            "واخيرا", "اخيرا", "وفي الاخير", "في الاخير", "وبالاخير", "بالاخير", "وفي النهايه", "في النهايه", "وبالنهايه", "بالنهايه", "وفي الختام", "في الختام", "ختاما", "اخر شي", "واخر شي",
            "بعدما", "وبعدما", "بعد ما", "وبعد ما", "ولما", "لما", "عندما", "وعندما", "حين", "وحين", "حينما", "اول ما", "واول ما",
            "وبعد شوي", "بعد شوي", "وبعد شويه", "بعد شويه", "وبعد لحظه", "بعد لحظه", "وبعد قليل", "بعد قليل", "بعد فتره", "وبعد فتره",
            "اول شي", "اولا", "اول شيء", "اول حاجه", "في البدايه", "بالبدايه", "وبعدها علي طول", "بعدها علي طول", "بعدها مباشره", "وبعدها مباشره", "ومباشره بعدها",
            "من بعد", "ومن بعد", "من بعدها", "ومن بعدها", "موراها", "ومورا", "مورا", "ومن بعد ذلك", "وبعد", "ثم بعدها", "وبعدين فجاه", "بعدين فجاه");
        W(K.Connector, "sudden", "فجاه", "وفجاه", "فجاءه", "وفجاءه", "بغته", "وبغته", "بشكل مفاجئ");
        W(K.And, "", "و", "كمان", "ايضا", "كذلك", "وكمان");
        W(K.Concurrent, "", "وهو", "وهي", "وهم", "وهما", "بينما", "اثناء", "وانت", "وانا", "واحنا", "ونحن", "في حين", "وفي نفس الوقت", "في نفس الوقت", "بنفس الوقت", "وبنفس الوقت", "وفي الوقت نفسه", "في الوقت نفسه", "اثناء ما", "حال");

        // ---- context clauses: "someone is behind you" -> look back, startled (not dropped).
        W(K.Cue, "look_back", "behind you", "behind me", "behind him", "behind her", "behind them", "behind us",
            "وراك", "وراه", "وراها", "ورايا", "وراكم", "وراكي", "خلفك", "خلفه", "خلفها", "خلفي", "خلفكم", "وراهم", "خلفهم");

        // ---- speed, direction
        W(K.Speed, "fast", "بسرعه", "سريع", "سريعا", "بسرعه كبيره", "بسرعه شديده", "بسرعه عاليه", "بسرعه جنونيه", "بسرعه البرق", "بشكل سريع", "بعجله", "بالعجل", "مستعجل", "علي عجل", "بخفه", "بلمح البصر", "طوالي", "بدغيا", "دغيا", "ع السريع", "علي السريع", "بتسارع", "بسرعتو", "بسرعته");
        W(K.Speed, "slow", "ببطء", "ببطئ", "بطيء", "بطئ", "بطيئا", "بشويش", "براحه", "بالراحه", "بهدوء", "شويه شويه", "شوي شوي", "علي مهله", "علي مهلك", "علي مهلو", "بتمهل", "بتاني", "بتأني", "برويه", "بالتدريج", "علي رواق", "بشويه", "رويدا", "رويدا رويدا",
            "عالراحه", "ع الراحه", "علي راحته", "علي راحتك", "بدون استعجال", "بتثاقل", "بحركه بطيئه", "ببطء شديد", "بخطي بطيئه", "علي كيفه", "علي كيفك", "علي رسله");
        W(K.Dir, "forward", "لقدام", "قدام", "للقدام", "للامام", "الي الامام", "لي قدام", "امام", "نحو الامام", "الامام", "قدامك", "باتجاه الامام", "بخط مستقيم", "علي طول", "دغري", "دوغري", "مباشره", "باستقامه", "مستقيم", "قدما");
        W(K.Dir, "backward", "ورا", "وراء", "لورا", "للورا", "للخلف", "الي الخلف", "لي ورا", "للوراء", "الي الوراء", "خلفا", "الخلف", "لخلف", "للور", "لور", "الوراء", "لوري", "بالخلف", "عكسيا", "بالاتجاه المعاكس", "الي ورا", "لوراء");
        W(K.Dir, "left", "يسار", "اليسار", "لليسار", "شمال", "للشمال", "علي اليسار", "يسري", "اليسري", "لجهه اليسار", "جهه اليسار", "ناحيه اليسار", "يسارا", "شمالا", "لشمال", "علي الشمال", "الايسر", "ايسر", "علي يسارك", "ع الشمال", "عالشمال", "عاليسار", "ع اليسار", "جهه الشمال", "الي اليسار", "الي الشمال");
        W(K.Dir, "right", "يمين", "اليمين", "لليمين", "علي اليمين", "يمني", "اليمني", "يمينا", "جهه اليمين", "ناحيه اليمين", "الايمن", "ايمن", "ع اليمين", "عاليمين", "علي يمينك", "الي اليمين");
        W(K.Dir, "up", "لفوق", "فوق", "للاعلي", "اعلي", "الي الاعلي", "لاعلي", "للسما", "للسماء", "عاليا", "في الهواء", "الي فوق", "بالاعلي", "الاعلي", "الي السماء");
        W(K.Dir, "down", "لتحت", "تحت", "للاسفل", "الي الاسفل", "لاسفل", "الاسفل", "الي تحت", "للارض", "نحو الارض");
        W(K.Dir, "side", "جانبا", "للجنب", "جنب", "للجانب", "لجنب", "جانبيا", "الي الجانب", "علي الجنب", "الجنب", "جنبا");
        W(K.Dir, "circle", "في دائره", "بشكل دائري", "حول نفسه", "حوالين نفسه", "حول نفسك", "دائري", "بدائره", "دوائر", "في دوائر", "حول نفسها", "علي نفسه");
        W(K.Dir, "in_place", "في مكانه", "في مكانك", "مكانه", "مكانك", "محله", "محلك", "بمكانه", "في نفس المكان", "في مكان واحد", "في نفس مكانه", "مكانها", "في محله", "بنفس المكان", "بمكانك", "في مكانها");

        // ---- manner and emotion
        W(K.Manner, "zombie", "زومبي", "الزومبي", "كالزومبي", "زمبي", "الزمبي", "كزومبي", "كالاموات", "مثل الاموات", "ميت حي");
        W(K.Manner, "old", "عجوز", "عجوزه", "شايب", "الشايب", "كبير في السن", "كبير السن", "كهل", "شيخ", "مسن", "حجي", "العجوز", "العجائز", "كالعجوز", "كالعجائز", "مثل العجائز", "عجايز", "جد", "جدو", "الجد", "حاج", "الحاج", "كبار السن", "الشيخ", "ختيار", "الختيار", "مسنه");
        W(K.Manner, "tired", "متعب", "تعبان", "تعبانه", "مرهق", "منهك", "مجهد", "كسلان", "نعسان", "ناعس", "بتعب", "بكسل", "بارهاق", "مكسل", "متعبه", "مرهقه", "منهكه", "معيي", "مهدود", "مهدود الحيل", "زهقان", "بتعب شديد", "مستنزف", "متثاقل", "بنعاس");
        W(K.Manner, "drunk", "سكران", "سكرانه", "سكر", "ثمل", "مخمور", "طافح", "مسطول", "السكران", "كالسكران", "مثل السكران", "بسكر", "سكارا", "سكاري", "ملخبط", "متعتع");
        W(K.Manner, "scared", "خايف", "خائف", "خايفه", "خائفه", "مرعوب", "مذعور", "فزعان", "مفزوع", "بخوف", "برعب", "رعب", "بذعر", "بفزع", "خوف", "خوفا", "خايفين", "خائفا", "مرتعب", "بهلع", "هلع", "مهلوع", "بخوف شديد", "فزعانه", "مرعوبه", "مذعوره", "خوفان", "خايفا", "كالخايف", "مثل الخايف");
        W(K.Manner, "nervous", "متوتر", "توتر", "بتوتر", "قلقان", "قلق", "بقلق", "قلقه", "متوتره", "مضطرب", "باضطراب", "مرتبك", "بارتباك", "متردد", "بتردد", "بحيره", "حيران", "محتار");
        W(K.Manner, "angry", "غاضب", "غضبان", "غضبانه", "معصب", "عصبي", "عصبيه", "بغضب", "بعصبيه", "بعنف", "مستشيط", "مغتاظ", "بغيظ", "منفعل", "بانفعال", "هايج", "بغضب شديد", "غاضبه", "بحده", "بعدوانيه", "عدواني");
        W(K.Manner, "happy", "فرحان", "فرحانه", "مبسوط", "مبسوطه", "سعيد", "سعيده", "بفرح", "بسعاده", "بابتسامه", "مسرور", "منبسط", "طربان", "مبتهج", "بمرح", "مرح", "بحماس", "حماس", "متحمس", "متحمسه", "بفرحه", "بانشراح", "منشرح", "فرحانين", "فرحا", "كالفرحان", "مثل الفرحان", "ببهجه", "بمرحه", "مستانس", "مستانسه", "مبسوطين", "بسرور", "بحيويه");
        W(K.Manner, "sad", "حزين", "حزينه", "مكتئب", "زعلان", "زعلانه", "مهموم", "بحزن", "بحسره", "متالم", "مكسور", "منكسر", "كئيب", "حزينا", "بكآبه", "بكابه", "بالم", "بقهر", "مغموم", "يائس", "بيأس", "مكتئبه", "بدموع", "دامع", "بخيبه", "محبط", "كالحزين", "مثل الحزين", "زعلانين");
        W(K.Manner, "proud", "بفخر", "فخور", "فخورا", "بكبرياء", "بغرور", "متكبر", "مغرور", "متكبره", "بتكبر", "متباهي", "بشموخ", "شامخ");
        W(K.Manner, "confident", "واثق", "بثقه", "واثقه", "بثقه عاليه", "بجراءه", "شجاع", "بشجاعه", "بتبختر", "متبختر", "بكل ثقه", "بعنفوان", "بهيبه", "مهيب", "جسور");
        W(K.Manner, "careful", "بحذر", "حذر", "حذرا", "بحرص", "حريص", "بانتباه", "منتبه", "بيقظه", "يقظ", "متيقظ", "بصمت", "بدون صوت", "بدون ضجه", "بتحفظ", "محتاط", "باحتياط", "بترقب", "بتوجس", "متوجس", "بحذر شديد", "بحيطه");
        W(K.Manner, "forceful", "بقوه", "قوي", "قويه", "بقوه شديده", "بكل قوته", "بكل قوه", "بشده", "بعزم", "بقسوه", "قاسيه", "قاسي", "شديد", "شديده", "عنيف", "عنيفه", "بقوه كبيره", "بعنف شديد", "بضراوه", "بوحشيه", "وحشي", "بكل عزمه", "بكل عزم", "بكل طاقته", "بكل طاقه");

        // ---- body parts (the side comes from a nearby right/left word: «يده اليمين»)
        W(K.Part, "hand", "يد", "ايد", "يده", "ايده", "بيده", "بايده", "يدك", "ايدك", "بيدك", "يدها", "ايدها", "بيدها", "يدي", "ايدي", "بيدي", "كف", "كفه", "كفها", "قبضه", "قبضته", "بقبضته", "قبضتك", "ذراع", "ذراعه", "بذراعه", "ذراعك", "ساعد", "ساعده", "كفك", "باليد", "بالايد", "اليد", "الايد", "اصبع", "اصبعه", "باصبعه", "اصبعك");
        W(K.Part, "leg", "رجله", "رجلك", "رجلها", "رجلي", "برجله", "برجلك", "برجلها", "ساق", "ساقه", "بساقه", "ساقك", "قدم", "قدمه", "بقدمه", "قدمك", "الساق", "القدم", "بالرجل", "بالقدم", "بالساق", "ركبه", "ركبته", "بركبته");
        W(K.Part, "head", "راس", "راسه", "براسه", "رأس", "رأسه", "برأسه", "راسك", "راسها", "براسك", "دماغ", "دماغه", "بدماغه", "جبهه", "جبهته", "بجبهته", "الراس", "بالراس", "الرأس", "راسي", "براسي");
        foreach (var w in new[] { "يدين", "يديه", "بيديه", "باليدين", "ايديه", "بايديه", "ايدين", "بالايدين", "ذراعيه", "بذراعيه", "قبضتيه", "بقبضتيه", "كفيه", "بكفيه", "بكفوفه" }) Put(Normalize(w), new Entry(K.Part, "hand", 1), w, false);
        foreach (var w in new[] { "رجلين", "رجليه", "برجليه", "قدميه", "بقدميه", "ساقيه", "بساقيه" }) Put(Normalize(w), new Entry(K.Part, "leg", 1), w, false);

        // ---- counts, repetition, steps, durations
        foreach (var w in new[] { "مرتين", "مرتان", "ضعفين" }) Put(Normalize(w), new Entry(K.Repeat, "", 2), w, false);
        Put(Normalize("ثلاثا"), new Entry(K.Repeat, "", 3), "ثلاثا", false);
        W(K.Times, "", "مرات", "مره", "مرا");
        W(K.StepWord, "", "خطوات", "خطوه", "خطى", "خطاوي");
        foreach (var w in new[] { "خطوتين", "خطوتان" }) Put(Normalize(w), new Entry(K.StepWord, "", 2), w, false);
        foreach (var (n, forms) in new (double, string[])[]
        {
            (0.25, new[] { "ربع", "quarter" }), (0.5, new[] { "half", "نص", "نصف" }), (1, new[] { "one", "a", "an", "واحد", "واحده", "وحده", "وحد" }), (2, new[] { "two", "اثنين", "اتنين", "اثنان", "ثنتين", "اثنتين", "تنين", "ثنين" }),
            (3, new[] { "three", "ثلاث", "ثلاثه", "تلات", "تلاته", "ثلث" }), (4, new[] { "four", "اربع", "اربعه" }), (5, new[] { "five", "خمس", "خمسه" }),
            (6, new[] { "six", "ست", "سته" }), (7, new[] { "seven", "سبع", "سبعه" }), (8, new[] { "eight", "ثمان", "ثماني", "ثمانيه", "تمن", "تمانيه" }),
            (9, new[] { "nine", "تسع", "تسعه" }), (10, new[] { "ten", "عشر", "عشره" }), (11, new[] { "eleven", "احدعش", "حداشر" }), (12, new[] { "twelve", "اثنعش", "اطنعش" }),
            (15, new[] { "fifteen", "خمستاشر", "خمسطعش" }), (20, new[] { "twenty", "عشرين", "عشرون" }), (30, new[] { "thirty", "ثلاثين", "تلاتين", "ثلاثون" }), (60, new[] { "sixty", "ستين", "ستون" }),
        })
            foreach (var f in forms) NumberWords[Normalize(f)] = n;
        W(K.Unit, "", "s", "sec", "secs", "second", "seconds", "ث", "ثانيه", "ثواني", "ثوان", "ثوانٍ", "لحظه", "لحظات");
        W(K.Unit, "60", "minute", "minutes", "min", "mins", "دقيقه", "دقايق", "دقائق");
        foreach (var (w, v) in new[] { ("ثانيتين", 2), ("ثانيتان", 2), ("دقيقتين", 120), ("دقيقتان", 120) }) Put(Normalize(w), new Entry(K.UnitWithCount, "", v), w, false);

        // ---- negation
        W(K.Negation, "", "بدون ما", "من غير ما", "من دون ما", "بلا ما", "بدون", "بلا", "من غير", "من دون", "دون ان", "دون", "لا", "لن", "لم", "ما", "مو", "مب", "مش", "ماني", "موب", "مهو", "ولا", "وما", "ومو", "ومش",
            "ماهو", "بغير", "بغير ما", "عدم", "بعدم", "ولن", "ولم");

        // ---- function words (understood, carry no action)
        W(K.Filler, "", "a", "an", "the", "person", "man", "woman", "guy", "girl", "boy", "character", "he", "she", "they", "it", "you", "i", "his", "her",
            "their", "your", "my", "to", "on", "of", "at", "in", "into", "onto", "with", "is", "are", "be", "very", "really", "there", "ground",
            "floor", "down", "around", "over", "from", "away", "for", "during", "about", "some", "someone", "somebody", "something", "please", "now", "again",
            "shoulder", "someone's", "there's", "startled", "himself", "herself", "hello", "hi", "out", "off", "just", "so", "that", "this", "as", "like", "by", "or", "but", "if", "its", "one", "two", "three");
        W(K.Filler, "", "شخص", "الشخص", "رجل", "الرجل", "راجل", "زول", "الزول", "ولد", "بنت", "هو", "هي", "هم", "هما", "نحن", "احنا", "انت", "انتا", "انتي", "انتم", "انا", "يا", "في", "فيه", "فيها", "علي", "الي", "لي", "له", "لها", "لهم", "به", "بها", "بهم",
            "منه", "منها", "منهم", "عليه", "عليها", "عليهم", "اليه", "اليها", "اليهم", "عنه", "عنها", "معه", "معها", "معهم", "من", "عن", "حتي", "لحد", "لين", "عشان", "كده", "كدا", "كذا", "هيك", "هيج", "هكذا", "خلاص", "الارض", "ارض", "الارضيه", "لمده", "مده", "تاني", "ثاني",
            "حد", "احد", "واحد", "شي", "شيء", "حاجه", "شيئا", "جاي", "جا", "زي", "مثل", "كانه", "كانها", "وكانه", "كانهم", "كان", "كانت", "يكون", "تكون", "كان", "طيب", "يلا", "هيا", "هلا", "هسه", "هسع", "الحين", "دلوقتي", "توا", "الان", "دحين", "دابا", "حاليا", 
            "هذا", "هذه", "هاذا", "هاذي", "هذي", "هادا", "هاد", "هاي", "دا", "دي", "ده", "ذلك", "تلك", "هنا", "هناك", "هنيك", "هناي", "او", "ام", "اذا", "لو", "ان", "انه", "انها", "اللي", "الذي", "التي", "الذين", "لكن", "ولكن", "بس", "قد", "لقد", "قبل", "عند", "مع", "كل", "كلها", "بعض",
            "جدا", "واجد", "وايد", "هواي", "كتير", "اوي", "كثير", "كثيرا", "قليلا", "شوي", "شويه", "تماما", "فقط", "عم", "عمال", "قاعد", "راح", "رح", "رايح", "حا", "راه", "ديما", "يعني", "مثلا", "نعم", "اه", "ايوه", "لا شي", "تعال", "واحده", "وحده", "وحد",
            "الشخصيه", "شخصيه", "الجندي", "جندي", "المحارب", "محارب", "البطل", "بطل", "اللاعب", "لاعب", "الكائن", "كائن", "الروبوت", "روبوت", "الفتاه", "فتاه", "المراه", "امراه", "الطفل", "طفل", "الشاب", "شاب", "شابه", "صبي", "ناس", "الناس", "الراجل", "الولد", "شرطي", "عسكري",
            "بعد", "وقت", "حيث", "هل", "ايش", "شو", "شنو", "وش", "ايه", "ليش", "ليه", "وين", "اين", "فين", "كيف", "نفسه", "نفسها", "بنفسه", "لنفسه", "بنفسها", "صوب");
    }

    static void LoadArabicLexicon()
    {
        // ---- locomotion
        AR("walk", "يمشي|مشي|ماشي|يتمشي|يسير|سير|سار|مشيه|يمشوا|يمشون|يتمشون|يتجول|تجول|جوله|يتنزه|يتبختر|يتمختر|يخطو|خطا|يتقدم|تقدم|يتحرك|تحرك|يروح|يجي|ييجي|يذهب|ذهب|يقبل|اقبل|يقترب|اقترب|يدخل|دخل|يخرج|خرج|يعبر|عبر|يجتاز|يتوجه|توجه|يتجه|اتجه|يتنقل|يختال|يتهادي|يمشي ذهابا وايابا|يروح ويجي|ياخذ جوله|يتحرك للامام|يمشي مشيه");
        AR("run", "يجري|جري|جاري|يجروا|يجرون|يركض|ركض|راكض|ركضا|يركضون|يهرب|هرب|هارب|يهربون|يشرد|شرد|شردو|يعدو|عدا|يتسابق|تسابق|سباق|يتسابقون|يهرول|هرول|هرولة|يفر|فر|فرار|يلوذ بالفرار|يلحق|لحق|يلاحق|يطارد|طارد|يطلق ساقيه للريح|يطلع جري|يجري جري|يتراكض|يسابق الريح|يسابق الزمن|يهرب هاربا");
        AR("sprint", "ينطلق|انطلق|انطلاق|ينطلقون|يندفع|اندفع|اندفاع|ينطلق بسرعه");
        AR("jump", "ينط|نطه|نطات|يتنطط|يقفز|قفز|قفزه|قفزات|ينقز|نقزه|يطفر|طفر|طفره|يثب|وثب|وثبه|يتقافز|تقافز|يتنقز|يحجل|حجل|ينط فوق|يقفز فوق|يقفز عاليا|يقفز للاعلي|يقفز في الهواء|ينط في الهواء");
        AR("crawl", "يزحف|زحف|زحفا|يحبو|حبا|يحبي|يدب علي اربع|يمشي علي اربع|علي اربع|يزحف علي بطنه|يتسلل زحفا|يتحرك زحفا|زاحف|يتقدم زحفا");
        AR("swim", "يسبح|سبح|سباحه|يعوم|عوم|يتسبح|سابح|يسبحون|يعومون|عايم|يسبح في الماء|يسبح في البحر|يسبح في المسبح|يسبح علي ظهره|يعوم في الماء|يحرك ذراعيه كالسباحه|يقلد السباحه|يمثل السباحه");
        AR("climb", "يتسلق|تسلق|يتشعبط|تشعبط|يطلع السلم|يطلع الشجره|يطلع الجبل|يطلع الجدار|يصعد|صعد|صعود|يرتقي|ارتقي|يتسلق السلم|يتسلق الشجره|يتسلق الجبل|يتسلق الجدار|يتسلق الحبل|يتسلق السور|يصعد السلم|يصعد الدرج|يطلع الدرج|يتسلق للاعلي|يتسلق لفوق");
        // ---- falling, dying
        AR("fall", "يقع|وقع|وقوع|وقعه|يطيح|طاح|طيحه|يسقط|سقط|سقوط|ينهار|انهار|انهيار|يتهاوي|تهاوي|يخر|خر|يهوي|هوي|يتكوم|تكوم|ينكب|يتعثر|تعثر|يتكعبل|تكعبل|يتعتر|يندفر|يتزحلق|تزحلق|ينزلق|انزلق|يرتمي|ارتمي|يوكع|وكع|يوقع|يقع علي الارض|يطيح علي الارض|يقع ارضا|يغمي عليه|اغمي عليه|يغمى عليه|يفقد وعيه|فقد وعيه|يفقد الوعي|يسقط ارضا|يسقط علي الارض|يتهالك|يقع علي وجهه|يقع علي ظهره|يطيح علي ظهره|يطيح علي وجهه|ينبطح ارضا|ينكفئ|ينطرح|ينطرح ارضا|يتطيح");
        AR("die", "يموت|موت|مات|ميت|ميته|يفطس|فطس|ينقتل|انقتل|يتوفي|مقتول|توفي|يهلك|هلك|هلاك|يستشهد|استشهد|يلفظ انفاسه|يلفظ انفاسه الاخيره|يسلم الروح|سلم الروح|يفارق الحياه|فارق الحياه|يروح فيها|راح فيها|يلاقي حتفه|لقي حتفه|يقضي نحبه|قضي نحبه|يصرع|يسقط قتيلا|يسقط صريعا|يطلع روحه|طلعت روحه|يموتون|ماتوا|يلقي مصرعه|لقي مصرعه|موته|مصرعه|مصرع|يموت فجاه|يلقى حتفه");
        AR("lie_down", "يستلقي|استلقي|يتمدد|تمدد|يتمدد علي الارض|يرقد|رقد|رقود|ينبطح|انبطح|يسطح|سطح|يتسطح|يتمطح|يستلقي علي ظهره|يستلقي علي بطنه|يرقد علي ظهره|يرقد علي الارض|يرقد علي السرير|يتمدد علي السرير|يتمدد علي ظهره|يتمدد علي بطنه|ممدد|ممدد علي الارض|مستلقي|مستلقيا|ينبطح علي بطنه|ينبطح علي الارض|يتمدد ارضا|يستلقي ارضا");
        AR("sleep", "ينام|نام|نايم|نائم|يغفو|غفا|يغط|يغط في النوم|نوم|النوم|يخلد للنوم|يستسلم للنوم|ينام بعمق|يغرق في النوم|يدخل في سبات|ينعس|نعس|يتناعس|ياخذ قيلوله|يقيل");
        // ---- postures
        AR("sit", "يقعد|قعد|يجلس|جلس|جالس|قعده|جلسه|يجلسون|يقعدون|يكعد|كعد|يتربع|تربع|يجلس علي الكرسي|يقعد علي الكرسي|يجلس علي الارض|يقعد علي الارض|يجلس باسترخاء|جلوس|الجلوس|القعود");
        AR("stand_up", "يقوم|قام|ينهض|نهض|يقوم من الارض|ينتصب|انتصب|ينتصب واقفا|يهب واقفا|هب واقفا|يقف علي رجليه|يقف علي قدميه|يقف مره ثانيه|يقف من جديد|يقف مجددا|يستيقظ|يعتدل|يعتدل واقفا|ينهض واقفا|نهوض|قيام|يقوم ويقف|يقف بعد السقوط|يحاول الوقوف");
        AR("idle", "يقف|واقف|وقوف|ثابت|ثبات|يستني|ينتظر|انتظار|يرتاح|اثبت|يثبت|يبقي واقف|يظل واقف|يظل واقفا|يبقي واقفا|يقف ثابت|يقف مكانه|يقف في مكانه|يقف بلا حركه|يقف دون حركه|يقف بدون حركه|بلا حركه|بدون حركه|يستريح|يسترخي|يهدا|يتنفس|تنفس|يتنفس بعمق|يتنفس بهدوء|يسكن|سكون|يسكت|يصمت|صمت|ساكن|يمكث|يلبث|يقف لحظه|يقف شوي|يقف قليلا|يبقي مكانه|يظل مكانه|ينتبه|يقف منتبها|يقف باستعداد|ياخذ نفس|ياخذ نفسا|ياخذ استراحه|يستعد|يتاهب|ارجع لوضع الاستعداد|يرجع لوضع الاستعداد|ترجع لوضع الاستعداد|يرجع الي وضع الاستعداد|يرجع لوضعه|ارجع لوضعك|يرجع لوضعه الاصلي|يرجع للوضع الطبيعي|يرجع لوضع القتال|يرجع لوضع الوقوف|وضع الاستعداد|يرجع لحاله الاستعداد|يرجع لوضعيه الاستعداد|يعود لوضع الاستعداد|يعود الي وضع الاستعداد|يعود لوضعه|يعود لوضعه الاصلي|يعود لوضع الوقوف|يرجع لوضع الراحه|يرجع للوضع الاول|يرجع لوضعه الاول|يعود للوضع الطبيعي|يرجع طبيعي|يرجع لوضعيه الوقوف");
        AR("stop", "وقف|قف|اقف|يوقف|توقف|يتوقف|تقف|توقفوا|ايقاف|يوقفون|يتوقفون|اوقف|يتسمر|تسمر|يتجمد|تجمد|يتسمر في مكانه|يتوقف عن الحركه|يتوقف عن المشي|يتوقف عن الجري|يوقف الحركه|وقفه|يقف فجاه|ينصدم ويتوقف|يتوقف فجاه");
        AR("crouch", "ينحني|انحني|انحناء|انحنى|يوطي|وطي|يوطى|يقرفص|قرفص|قرفصاء|يدنقر|دنقر|ينخفض|انخفض|يطاطي|طاطي|يطأطئ|يطاطي راسه|يتكور|تكور|يتقوقع|تقوقع|يختبئ|اختبئ|يتخبي|يستخبي|يتخبا|اختبي|يختبي|يتوارى|يتمترس|يلتصق بالارض|يجلس القرفصاء|يقعد القرفصاء|يقعد علي عقبيه|يجلس علي عقبيه|ينحني للارض|ينحني لتحت|ينحني قليلا|ينزل لتحت|ينزل للارض|يتكوم علي نفسه|ينخفض للارض|يوطي راسه|يدنقر لتحت");
        AR("kneel", "يجثو|اجثو|جثا|يجثم|جثم|يركع|اركع|ركع|ركوع|يبرك|ابرك|برك|يبرك علي ركبتيه|يجثو علي ركبتيه|يجثو علي ركبه|ينزل علي ركبته|ينزل علي ركبتيه|يركع علي ركبته|يسجد|اسجد|سجد|سجود|يستسلم راكعا|يخر ساجدا");
        AR("bow", "ينحني احتراما|ينحني تحيه|ينحني للتحيه|انحني احتراما|ينحني لتحيه|ينحني تعظيما|يحني راسه احتراما|يحني ظهره احتراما|ينحني امام|ينحني للجمهور|ينحني للحضور|ينحني للملك|ينحني للجميع|ينحني شكرا|ينحني امتنانا|ينحني تقديرا|ينحني ادبا|ينحني بادب|ينحني بوقار|يعظم|تعظيم|يقدم تعظيما|يؤدي التعظيم");
        // ---- turning, looking
        AR("turn_around", "يلف|تلف|يدور|يستدير|استدير|استدار|يدور ورا|لف ورا|دور ورا|لف لورا|دور لورا|يلف ورا|يلف لورا|يدور لورا|يلف للخلف|يدور للخلف|يلف للوراء|يعطي ظهره|يوليه ظهره|يدير ظهره|يدير جسمه|يستدير للخلف|يستدير للوراء|يلف دوره|يلف دوره كامله|يعمل دوره|دوره كامله|يغير اتجاهه|ينعطف|انعطف|يتدور|يلتف|التف");
        foreach (var look in new[] { "يطالع", "طالع", "يشوف", "شوف", "بص", "يبص", "عاين", "يعاين", "يناظر", "ناظر", "ينظر", "انظر", "نظر", "يتطلع", "تطلع", "يحدق", "حدق", "يتفرج", "يراقب", "راقب", "يرمق", "يطل", "يبصبص", "يتلفت", "اتلفت", "يلتفت", "التفت", "يشاهد", "شاهد", "يتابع", "يلاحظ", "يتفحص" })
        {
            foreach (var back in new[] { "ورا", "وراء", "وراه", "وراك", "لورا", "خلف", "خلفه", "خلفك", "للخلف", "للوراء", "الي الوراء", "الي الخلف", "للور", "وراها", "خلفها" })
                A("look_back", look + " " + back);
            foreach (var around in new[] { "حوله", "حواليه", "حواليك", "حولك", "حوالين", "حولها", "حواليها", "يمين وشمال", "يمينا وشمالا", "في كل مكان", "في المكان", "في الارجاء", "يمين ويسار", "شمال ويمين", "في كل اتجاه", "في كل الاتجاهات", "من حوله", "من حواليه", "للجانبين", "علي الجانبين", "بالمكان", "في المحيط", "الي كل مكان" })
                A("look_around", look + " " + around);
        }
        AR("look_back", "يلتفت|التفت|تلتفت|اتلفت|يتلفت|تتلفت|يلتفت للخلف|يلتفت لورا|يلتفت وراه|يلتفت ورا|يتلفت ورا|يتلفت للخلف|يتلفت لورا|التفت ورا|التفت للخلف|يراجع خلفه|يشيك ورا|يحس بحد وراه|يحس انه احد وراه|يشعر بشخص خلفه|يسمع صوت وراه|يسمع صوت خلفه|ينظر خلفه|ينظر ورائه|ينظر للخلف|يلتفت خلفه|يلتفت وراءه|يلتفت الي الوراء|يلتفت الي الخلف|التفاته|التفاته للخلف");
        AR("look_around", "يتفحص المكان|يستكشف|استكشف|يستطلع|يستكشف المكان|يتفقد المكان|يتفقد|يبحث|ابحث|بحث|يبحث حوله|يفتش|فتش|يدور علي|يدور عن|يتجسس|يتلصص|يراقب المكان|يراقب المحيط|يمسح المكان|يمسح المنطقه|يتفحص المحيط|يتلفت في المكان|يتلفت حوله|يتلفت حواليه|يتلفت يمين وشمال|يتلفت يمين ويسار|يتلفت في كل مكان|يحرك راسه يمين ويسار|يحرك راسه يمينا وشمالا|يتحقق من المكان|يتحقق من المحيط|يتفقد الارجاء|يتفقد المحيط|يستكشف المنطقه|يستطلع المكان");
        AR("look", "يطالع|طالع|يشوف|بص|يبص|عاين|يعاين|يناظر|ناظر|ينظر|انظر|نظر|يتطلع|تطلع|يحدق|حدق|يتفرج|يرمق|يطل|يبصبص|يشاهد|شاهد|يتابع|يلاحظ|يلمح|يلقي نظره|يرمي نظره|يوجه نظره|يوجه بصره|يحرك عينيه|نظره");
        AR("step_back", "يرجع|رجع|يتراجع|تراجع|يرجع لورا|ارجع لورا|يرجع ورا|ارجع ورا|خطوه لورا|خطوه للخلف|خطوتين لورا|يتقهقر|تقهقر|يرجع للخلف|يرجع للوراء|يرجع خطوه|يرجع خطوتين|يتراجع للخلف|يتراجع للوراء|يتراجع خطوه|يتراجع خطوتين|يبتعد|ابتعد|يبعد|ابعد|يتنحي|تنحي|ينسحب|انسحب|يخطو للخلف|يخطو للوراء|يخطو لورا|يمشي للخلف|يمشي للوراء|يمشي لورا|يمشي ورا|يمشي القهقري|يعود للخلف|يعود للوراء|يعود لورا|يعود خطوه|يتزحزح|يتاخر|يتأخر|يرجع شوي|يرجع قليلا|يرجع شويه|يتراجع شوي|يتراجع قليلا|يتراجع ببطء|يتراجع بحذر|يتراجع بخوف|يتراجع مذعورا|يتراجع مرعوبا|يتراجع فزعا|يتراجع خوفا|ينكمش للخلف");
        // ---- combat
        AR("kick", "يركل|ركل|ركله|ركلات|يرفس|رفس|رفسه|رفسات|يشوت|شوت|شوته|يشلت|شلوت|يرفص|رفص|يضرب برجله|يضرب بالرجل|يضرب بقدمه|يركل برجله|يركل بقدمه|يرفس برجله|يدعس|دعس|يلبط|لبط|يوجه ركله|يسدد ركله|يعطي ركله|يعطيه ركله|يعطيه رفسه|ركله قويه|يرفس بقوه|يركل بقوه|يركل الكره|يشوت الكره|يضرب الكره برجله|ركلات متتاليه|يركل بالقدم|يركل بالرجل");
        AR("punch", "يضرب|ضرب|ضربه|ضربات|يلكم|لكم|لكمه|لكمات|بوكس|يبوكس|بوكسه|يهاجم|هاجم|هجوم|يهجم|هجم|يعتدي|اعتدي|اعتداء|يتعارك|تعارك|يتخانق|تخانق|يتشاجر|تشاجر|يتقاتل|تقاتل|يقاتل|قاتل|قتال|يتضارب|تضارب|يتصارع|يصارع|يخبط|خبط|خبطه|يلطش|لطش|يلاكم|لاكم|ملاكمه|يعطي لكمه|يعطيه لكمه|يعطي بوكس|يعطيه بوكس|يسدد لكمه|يسدد ضربه|يوجه لكمه|يوجه ضربه|يضرب لكمه|يضرب بوكس|يضرب بقبضته|يضرب بيده|يلكم بقبضته|يكيل الضربات|يكيل اللكمات|يوجه ضربات|يسدد لكمات|يضرب بقوه|يضرب بعنف|يضرب بايده|يضرب باليد|يضرب الكيس|يلكم الكيس|يلكم الهواء|يضرب الظل|ضربه قويه|لكمه قويه|يطعمه بوكس|يبطش|بطش");
        AR("slap", "يصفع|صفع|صفعه|صفعات|يلطم|لطم|لطمه|لطمات|يكفخ|كفخ|يعطي كف|يعطيه كف|يضرب كف|يضرب بالكف|يضرب بالقلم|يعطيه قلم|يصفع الوجه|يلطم وجهه|يلطم خده");
        AR("slash", "يضرب بالسيف|يضرب بسيفه|يلوح بالسيف|يلوح بسيفه|يهوي بالسيف|يهوي بسيفه|يقطع بالسيف|يقطع بسيفه|يشق بالسيف|يجرح بالسيف|يهوي بالفاس|يضرب بالفاس|يضرب بالساطور|ضربه بالسيف|ضربه سيف|ضربه بالفاس|سيف|سيفه|بالسيف|بسيفه|يسل سيفه|يسحب سيفه|يشهر سيفه|يتبارز|تبارز|يبارز|بارز|مبارزه|يتبارزون|يقاتل بالسيف|يقاتل بسيفه|يلعب بالسيف|يلعب بسيفه|يفلق|يقد");
        AR("stab", "يطعن|طعن|طعنه|طعنات|يغرز|غرز|غرزه|يغرس|غرس|يخزق|خزق|يوخز|وخز|يطعن بالسكين|يضرب بالسكين|يضرب بسكينه|يغرز سكينه|يغرس سكينه|يغرز الخنجر|يطعن بالخنجر|يضرب بالخنجر|طعنه بالسكين|طعنه سكين|طعنه خنجر|ضربه بالسكين|سكين|سكينه|بالسكين|بسكينه|خنجر|خنجره|بالخنجر|بخنجره|يطعن بالرمح|يطعن بالحربه|يرمح|رمح");
        AR("block", "يصد|صد|يدافع|دافع|دفاع|يحجب|حجب|يتصدي|تصدي|يتصدى|يحمي نفسه|احمي نفسك|يحمي وجهه|يحمي راسه|يغطي وجهه|يغطي راسه|يحتمي بيديه|يحتمي بذراعيه|يرفع يديه للدفاع|يرفع ذراعيه للدفاع|يرفع ذراعيه|يرفع يديه امام وجهه|يتقي|اتقي|يتترس|بلوك|يبلوك|يسد الضربه|يصد الضربه|يصد الهجوم|يصد اللكمه|يصد الركله|يصد السيف|يمنع الضربه|يمنع الهجوم|يتلقي الضربه بيديه|يتلقي الضربه بذراعيه|يقي نفسه|يقي وجهه|يقي راسه|يحجب الضربه");
        AR("fight_stance", "وضع القتال|وضعيه القتال|يتخذ وضعيه القتال|ياخذ وضعيه القتال|ياخذ وضع القتال|يتخذ وضع القتال|يتاهب للقتال|يستعد للقتال|استعد للقتال|يرفع قبضتيه|يرفع قبضته|يرفع قبضاته|وضع الملاكمه|وضعيه الملاكمه|ياخذ وضع الملاكمه|يتخذ وضع الملاكمه|وضعيه الدفاع|وضع الدفاع|يتخذ وضعيه الدفاع|وضع الهجوم|وضعيه الهجوم|وضع الاشتباك|وضع المبارزه|يجهز نفسه للعراك|يجهز نفسه للقتال|يستعد للمعركه|يستعد للمواجهه|يتاهب للمواجهه|يتاهب للمعركه|يستعد للعراك|يستعد للهجوم|يتاهب للهجوم|وضع المقاتل|وضع المحارب|جاهز للقتال|جاهز للعراك|مستعد للقتال|يرفع حراسته|يرفع الحراسه|يتحفز للقتال|يتحفز للهجوم|متحفز للقتال|يكور قبضتيه|يشد قبضتيه|يضم قبضتيه|يسخن للقتال");
        AR("dodge", "يتفادي|تفادي|يتفادى|يراوغ|راوغ|يزوغ|زوغ|يتهرب|يتحاشي|تحاشي|يتحاشى|يتجنب|تجنب|يتملص|يميل جانبا|يميل للجنب|يبتعد جانبا|يتحرك جانبا|يتحرك للجنب|يتفادي الضربه|يتفادي الهجوم|يتفادي اللكمه|يتفادي الركله|يتفادي السيف|يتفادي الرصاصه|يتفادي الرصاص|يتهرب من الضربه|يتهرب من الهجوم|يتجنب الضربه|يتجنب الهجوم|يزوغ من الضربه|يراوغ الضربه|مراوغه|مراوغات|يتملص من الضربه|ينحرف عن الضربه|ينحني تفاديا|ينزاح جانبا|يزيح جانبا|يخطو جانبا|يتحرك للجانب|يخرج من خط النار|يبتعد عن الضربه|يبتعد عن الهجوم");
        AR("get_hit", "يتالم|تالم|يتوجع|توجع|يتاوه|تاوه|يتاذي|تاذي|يتاذى|يصاب|اصيب|اصابه|يتصاب|اتصاب|ينضرب|انضرب|اتضرب|يتضرب|يتاثر بالضربه|يتعور|يصرخ من الالم|يصرخ من الوجع|يصيح من الالم|يتلوي من الالم|يمسك بطنه|يمسك صدره|يمسك وجهه|يمسك جنبه|يمسك مكان الضربه|يمسك مكان الاصابه|يرتد من الضربه|يرتد بعد الضربه|يهتز من الضربه|يترنح من الضربه|يتراجع بعد الضربه|يتراجع من الضربه|يتراجع متالما|يتراجع مصابا|ينصدم|يتلقي صدمه|ياخذ صدمه|يصاب بجرح|مجروح|جريح|مصاب");
        Cross("get_hit", new[] { "يتلقي", "تلقي", "ياخذ", "اخذ", "ياكل", "يستقبل", "يتعرض", "يصاب", "يتحمل", "يتلقاها" }, new[] { "ضربه", "ضربات", "لكمه", "لكمات", "ركله", "ركلات", "طعنه", "رصاصه", "رصاص", "صفعه", "صدمه", "ضربه قويه", "لكمه قويه", "ركله قويه", "لضربه", "للكمه", "لركله", "لطعنه", "لرصاصه", "لصفعه", "لصدمه", "بضربه", "بلكمه", "بركله", "بطعنه", "برصاصه", "بصفعه", "بصدمه", "ضربه في وجهه", "لكمه في وجهه", "لكمه في بطنه", "ضربه في بطنه", "ضربه علي راسه", "ضربه في صدره", "ركله في بطنه", "رصاصه في صدره", "رصاصه في كتفه", "رصاصه في رجله", "رصاصه في ظهره", "طعنه في ظهره", "طعنه في بطنه" });
        Cross("get_hit", new[] { "يتالم", "يتوجع", "يتاوه" }, new[] { "من الضربه", "من اللكمه", "من الركله", "من الطعنه", "من الرصاصه", "من الصفعه", "من الصدمه", "من الالم", "من الوجع", "بعد الضربه", "بعد اللكمه" });
        AR("startle", "يتفاجا|تفاجا|يفاجا|يفزع|فزع|يفزع فجاه|يرتعب|ارتعب|يخاف|خاف|يجفل|جفل|ينتفض|انتفض|ينتفض فزعا|يتخض|اتخض|يتخلع|اتخلع|يرتاع|ارتاع|يستغرب|استغرب|يصاب بالذعر|يصاب بالرعب|يصاب بالفزع|يصاب بالخوف|يصاب بالهلع|يهلع|يذعر|ذعر|يشهق|شهق|يشهق من الخوف|يشهق فزعا|يقفز من الخوف|يقفز فزعا|ينط من الخوف|ينط فزعا|يتراجع فزعا|يتراجع مفزوعا|يتجمد من الخوف|يتجمد خوفا|يتجمد رعبا|يفزع من الصوت|يخاف من الصوت|يجفل من الصوت|ينتفض من الصوت|يتفاجأ|يتفاجا فجاه");
        Cross("startle", new[] { "يحس", "يشعر", "ياخذ", "يتملكه", "يعتريه" }, new[] { "بالخوف", "بالرعب", "بالفزع", "بالذعر", "بالهلع", "بخوف", "برعب", "بفزع", "بذعر" });
        AR("tremble", "يرتجف|ارتجف|ارتجاف|يرتعش|ارتعش|ارتعاش|يرجف|رجف|رجفه|يرتعد|ارتعد|ارتعاد|يقشعر|اقشعر|قشعريره|يتقشعر|يرتجف خوفا|يرتجف من الخوف|يرتجف من البرد|يرتعش خوفا|يرتعش من الخوف|يرتعش من البرد|يرتعد خوفا|يرتعد من الخوف|يرتعد من البرد|ترتجف يداه|ترتجف ساقاه|ترتجف ركبتاه|ترتعش يداه|ترتعد فرائصه");
        AR("limp", "يعرج|عرج|أعرج|يخمع|اخمع|خمع|عرجان|بعرج|اعرجاج|يعرج في مشيته|يعرج بقدمه|يعرج برجله|يمشي بعرج|يمشي اعرج|يمشي عرجان|يعرج وهو يمشي|يتعرج|يخمع في مشيته|يخمع برجله|يمشي يخمع|يعرج بسبب اصابه|يعرج من الم|يتكئ علي رجل واحده");
        Cross("limp", new[] { "يجر", "يسحب", "يجرجر" }, new[] { "رجله", "قدمه", "رجليه", "ساقه", "رجلها", "قدمها", "ساقها" });
        AR("stagger", "يترنح|ترنح|ترنحه|مترنح|يتخبط|يتمايل بصعوبه|يمشي مترنح|يمشي بترنح|يمشي وهو يترنح|يمشي ويترنح|يترنح في مشيته|يتأرجح|يتارجح|يتمايل يمين وشمال|يتمايل يمين ويسار|يتمايل بشكل غير متزن|يمشي بشكل غير متزن|يفقد توازنه|فقد توازنه|يفقد التوازن|يختل توازنه|اختل توازنه|يتخبط في مشيته|يتخبط وهو يمشي|يتخبط ويمشي|يتعثر في مشيته|يتعثر وهو يمشي|يتمايل وهو يمشي|يتمايل في مشيته");
        AR("sneak", "يتسلل|تسلل|يتسحب|تسحب|يتخفي|تخفي|يتخفى|يتسلل بهدوء|يتسلل بحذر|يتسلل بخفه|يتسلل بصمت|يتسلل بدون صوت|يتسلل خلسه|يتسلل علي اطراف اصابعه|يمشي علي اطراف اصابعه|يمشي علي اطراف الاصابع|يمشي علي رؤوس اصابعه|يمشي علي رؤوس الاصابع|يمشي علي اطراف قدميه|يمشي علي رؤوس قدميه|يمشي خلسه|يمشي بخفيه|علي اطراف اصابعه|علي اطراف الاصابع|علي رؤوس اصابعه|علي رؤوس الاصابع|علي اطراف قدميه|علي رؤوس قدميه|بخفيه|بتسلل|خلسه|يترصد|يتربص|يتعقب|يقتفي اثره|يخطو علي اطراف اصابعه");
        // ---- hands, gestures
        AR("wave", "يلوح|لوح|تلويح|تلويحه|ياشر|اشر|يشاور|شاور|باي|تحيه|يحيي|يحيى|حيي|يسلم علي|يسلم|سلم|يلوح بيده|يلوح بايده|يلوح بكفه|يلوح للجمهور|يلوح للناس|يلوح لهم|يلوح له|يلوح لها|يهز يده|يهز ايده|يهز كفه|يحرك يده|يحرك ايده|يرفع يده ملوحا|يرفع يده للتحيه|يودع|ودع|يودعهم|يقول باي|يقول وداعا|يقول مع السلامه|يقول مرحبا|يقول هلا|يقول اهلا|يقول سلام|يقول السلام عليكم|يقول هاي|يقول هلو|يرحب|رحب|يرحب بهم|يستقبل|يحيي بيده|يحيي الجمهور|يلوح مودعا|يلوح مرحبا|يلوح محييا|يلوح للكاميرا");
        AR("point", "يشير|اشار|اشاره|تشير|يؤشر|يشير الي|يشير علي|يشير نحو|يشير باتجاه|يشير بيده|يشير بايده|يشير بكفه|يشير باصبعه|يؤشر الي|يؤشر علي|يؤشر نحو|يؤشر بيده|يؤشر باصبعه|يدل|دل|يدل علي|يدل الي|يدل بيده|يدل باصبعه|يومئ الي|يومئ نحو|يوجه اصبعه|يوجه يده|يصوب اصبعه|يشاور علي|يأشر علي|يرفع اصبعه|يمد اصبعه|يمد يده نحو|يمد يده الي|يمد ايده نحو|يمد يده|يمد ايده|يمد ذراعه|يمد ذراعه نحو|يمد ذراعيه|يتهم|اتهم|يتهمه باصبعه");
        AR("salute", "يؤدي التحيه العسكريه|يؤدي التحيه|يؤدي التحيه للعلم|يرفع يده بالتحيه|تحيه عسكريه|يحيي العلم|سلام عسكري|يسلم عسكري|يضرب تحيه|يقدم التحيه|يعطي التحيه|يحيي بالتحيه العسكريه|يحيي عسكريا|يؤدي سلاما عسكريا|يؤدي السلام العسكري|يقدم السلام العسكري|السلام العسكري|التحيه العسكريه|تحيه الجنود|تحيه الجندي|يرفع يده الي جبينه|يرفع يده الي راسه|يضع يده علي جبينه|يضع يده عند جبينه|يرفع يده عند جبينه|يقف باحترام ويحيي");
        AR("clap", "يصفق|صفق|تصفيق|يصقف|صقف|تصقيف|يسقف|سقف|تسقيف|يطبل|طبل|يضرب كفا بكف|يضرب بكفيه|يضرب كفيه|يصفق بيديه|يصفق بايديه|يصفق بكفيه|يصفق بكفوفه|يصفق باليدين|يصفق بالايدين|يصفق بالكفين|يصفق للجمهور|يصفق للفائز|يصفق لهم|يصفق له|يصفق لها|يصفق ويهتف|يصفق ويشجع|يصفق بحماس|يصفق بقوه|يصفق بحراره|يصفق طويلا|يصفق باستمرار");
        AR("dance", "يرقص|رقص|رقصه|رقصات|يتمايل|يدبك|دبك|دبكه|يتراقص|تراقص|يهز وسطه|يهز خصره|يهز جسمه|يرقص العرضه|يرقص السامري|يرقص الدبكه|يرقص الباليه|يرقص الفلامنكو|يرقص الهيب هوب|يرقص التانغو|يرقص الفالس|يرقص السالسا|يرقص الشعبي|يتمايل مع الموسيقي|يرقص علي الموسيقي|يتحرك مع الموسيقي|يرقص بحماس|يرقص فرحا|يرقص من الفرح|يرقص وهو يضحك|يرقص ويغني|يغني ويرقص|يرقص ويصفق|يصفق ويرقص|يرقص ويتمايل|يتمايل ويرقص");
        AR("cheer", "يهتف|هتف|هتاف|يشجع|شجع|تشجيع|يهلل|هلل|تهليل|يزغرد|زغرد|زغروده|زغاريد|يهتف فرحا|يهتف بحماس|يرفع يديه فرحا|يرفع يديه عاليا|يرفع ذراعيه عاليا|يرفع ذراعيه فرحا|يرفع قبضتيه فرحا|يرفع قبضته فرحا|يرفع قبضتيه عاليا|يهلل فرحا|يزغرد فرحا|يشجع فريقه|يشجع الفريق|يشجع المنتخب|يهتف حماسا|يهلل حماسا|يشجع بحماس|يهتف بصوت عالي|يهتف باعلي صوته|يهلل بحماس|يهلل باعلي صوته|يرفع يديه للسماء");
        AR("celebrate", "يحتفل|احتفل|احتفال|احتفالا|يفرح|افرح|يبتهج|ابتهج|ابتهاج|ينتصر|انتصر|انتصار|يفوز|فاز|فوز|يحتفل بالفوز|يحتفل بالنصر|يحتفل بالانتصار|يحتفل بالنجاح|يحتفلون|احتفلوا|يحتفل فرحا|يحتفل بفرح|يحتفل بحماس|يرفع الكاس|يرفع الكأس|يرفع الكاس عاليا|يرفع الجائزه|يرفع الجائزه عاليا|يرفع قبضته منتصرا|يرفع قبضتيه منتصرا|يرفع ذراعيه منتصرا|يرفع يديه منتصرا|وضع النصر|وضعيه النصر|وضعيه الفوز|وضع الفوز|يتخذ وضعيه النصر|ياخذ وضعيه النصر|يتخذ وضع النصر|رقصه النصر|رقصه الفوز|رقصه الفرح|رقصه الاحتفال|يتسلم الكاس|يتسلم الجائزه|يستلم الكاس|يستلم الجائزه");
        AR("cry", "يبكي|ابكي|بكي|بكا|بكاء|يبكون|يبجي|ابجي|بجي|يعيط|عيط|اعيط|عياط|ينتحب|انتحب|نحيب|يندب|اندب|ندب|يدمع|تدمع|تدمع عيناه|يذرف الدموع|ذرف الدموع|تذرف عيناه|تنهمر دموعه|تسيل دموعه|تدمع عيونه|ينوح|نوح|انوح|نواح|يتباكي|يجهش بالبكاء|اجهش بالبكاء|يبكي بحرقه|يبكي بصمت|يبكي بصوت عالي|يبكي بحزن|يبكي كالطفل|يمسح دموعه|يمسح عينيه|يمسح عيونه|يجفف دموعه|باكي|باكيا");
        AR("laugh", "يضحك|اضحك|ضحك|ضحكه|ضحكات|يقهقه|قهقه|قهقهه|يكركر|كركر|كركره|يضحك بصوت عالي|يضحك بقوه|يضحك من قلبه|يضحك من كل قلبه|يموت من الضحك|يموت ضحك|يفطس ضحك|يفطس من الضحك|يتقطع من الضحك|يتمرغ من الضحك|يمسك بطنه من الضحك|يمسك بطنه ويضحك|يضحك ويمسك بطنه|ضاحك|ضاحكا|يضحك بملء فمه|يضحك بصخب");
        AR("smile", "يبتسم|ابتسم|ابتسامه|يتبسم|تبسم|يبتسم ابتسامه|يبتسم بخفه|يبتسم بهدوء|يبتسم بلطف|يبتسم بسعاده|يبتسم بفرح|يبتسم بمكر|يبتسم بخبث|يبتسم بسخريه|يبتسم بحنان|يبتسم بحب|يبتسم بود|يبتسم بدفء|يبتسم بخجل|يبتسم ابتسامه خفيفه|يبتسم ابتسامه عريضه|يبتسم ابتسامه واسعه|مبتسم|مبتسما|يبتسمون|ابتسموا|يفتر ثغره|يرسم ابتسامه");
        AR("think", "يفكر|فكر|تفكير|تفكيره|يتفكر|يتامل|تامل|يتأمل|تأمل|يحك راسه|حك راسه|يحك دماغه|يحك رأسه|يفكر بعمق|يفكر مليا|يفكر في الامر|يفكر في الموضوع|يفكر في الحل|يضع يده علي ذقنه|يضع يده علي دقنه|يمسك ذقنه|يمسك دقنه|يمسك ذقنه ويفكر|يفرك ذقنه|يفرك دقنه|يمسد ذقنه|مفكر|مفكرا|حائر|شارد الذهن|يسرح في التفكير|يغرق في التفكير|يغوص في التفكير|يستغرق في التفكير|يتامل بعمق|يتأمل بعمق");
        AR("talk", "يتكلم|تكلم|كلام|يتحدث|تحدث|حديث|يحكي|احكي|حكى|يقول|قل|قال|يتكلمون|يتحدثون|يحكون|يقولون|يدردش|دردش|دردشه|يسولف|سولف|سوالف|سالفه|يتناقش|تناقش|نقاش|يناقش|ناقش|يتحاور|تحاور|حوار|يحاور|يخاطب|مخاطبه|يكلم|كلم|يكلمه|يكلمها|يكلمهم|يكلمك|يكلمني|يكلمنا|يشرح|شرح|يفسر|يوضح|يخطب|خطبه|يحاضر|محاضره|يتكلم مع نفسه|يكلم نفسه|يحدث نفسه|يتحدث مع نفسه|يتكلم مع صديقه|يتكلم بيديه|يتكلم بايديه|يتكلم بالايدي|يتكلم ويشير|يتكلم ويلوح|يتكلم ويحرك يديه");
        AR("phone_call", "يتصل|اتصل|يتصلون|اتصال|يتصل بصديقه|يتصل بشخص|مكالمه|مكالمات|يجري مكالمه|يجري اتصال|يجري مكالمه هاتفيه|يرد علي التلفون|يرد علي الجوال|يرد علي المكالمه|يرد علي الهاتف|يرد علي الموبايل|يرد علي الاتصال|يجيب علي التلفون|يجيب علي الجوال|يجيب علي المكالمه|يجاوب التلفون|يجاوب الجوال|يجاوب المكالمه|يرد التلفون|يرد الجوال|يرد المكالمه|يتكلم بالتلفون|يتكلم في التلفون|يتكلم علي التلفون|يتكلم بالجوال|يتكلم في الجوال|يتكلم علي الجوال|يتكلم بالهاتف|يتكلم في الهاتف|يتكلم علي الهاتف|يتكلم بالموبايل|يتكلم في الموبايل|يتحدث بالتلفون|يتحدث في التلفون|يتحدث بالجوال|يتحدث في الجوال|يتحدث بالهاتف|يتحدث في الهاتف|يتحدث هاتفيا|يتكلم هاتفيا|يكلم بالتلفون|يكلم بالجوال|يتلفن|تلفن|يدق تلفون|يدق جوال|يدق هاتف|يدق موبايل|يمسك الجوال|يمسك التلفون|يمسك الهاتف|يمسك الموبايل|يمسك جواله|يمسك تلفونه|يمسك هاتفه|يمسك موبايله|يضع الجوال علي اذنه|يضع التلفون علي اذنه|يضع الهاتف علي اذنه|يضع جواله علي اذنه|يضع تلفونه علي اذنه|يرفع الجوال الي اذنه|يرفع التلفون الي اذنه|يرفع جواله الي اذنه");
        AR("shout", "يصرخ|اصرخ|صرخ|صراخ|صرخه|صرخات|يصيح|صيح|اصيح|صياح|صيحه|صيحات|يزعق|زعق|ازعق|زعيق|زعقه|زعقات|ينادي|انادي|ينادي بصوت عالي|ينادي باعلي صوته|يهوش|هوش|هوشه|يعلي صوته|يرفع صوته|يرفع صوته عاليا|يصرخ بصوت عالي|يصرخ باعلي صوته|يصرخ بقوه|يصرخ بغضب|يصرخ بخوف|يصرخ فرحا|يصرخ غضبا|يصرخ خوفا|يصيح بصوت عالي|يصيح باعلي صوته|يزعق بصوت عالي|يزعق باعلي صوته|صارخ|صارخا|يصرخون|يصيحون|يزعقون");
        AR("sing", "يغني|غني|اغني|غناء|اغنيه|يغنون|يدندن|دندن|دندنه|يترنم|ترنم|ترنيمه|يغني اغنيه|يغني بصوت عالي|يغني بصوت جميل|يغني بفرح|يغني بحماس|يغني ويرقص|يغني ويصفق|يصفق ويغني|يغني لنفسه|يغني علي المسرح|يغني في الحفل|يغني في الحمام|ينشد|انشد|انشاد|نشيد|يرتل|رتل|ترتيل|مغني|مغنيا|يصفر|صفر|يصفر لحنا|يصفر بلحن|يصفر اغنيه|يمشي ويصفر");
        // ---- objects
        AR("throw", "يرمي|رمي|رميه|يقذف|قذف|يحدف|حدف|حدفه|يقزف|قزف|يرمي الكره|يرمي الحجر|يرمي الحجره|يرمي حجر|يرمي القنبله|يرمي الرمح|يرمي السهم|يرمي الكيس|يرمي الشنطه|يرمي الصندوق|يرمي بعيدا|يرمي بقوه|يرمي لقدام|يرمي للامام|يرمي الكوره|يرمي الطابه|يرمي شي|يرمي شيء|يرمي الشي|يرمي الحاجه|يرمي بعنف|يرمي بكل قوته");
        AR("catch", "يمسك|مسك|يقبض|قبض|يتلقف|تلقف|يستقبل الكره|يلحق الكره|يمسك الكره|يمسك الكوره|يمسك الطابه|يقبض علي الكره|يتلقف الكره|يمسك بيديه|يمسك بايديه|يمسك باليدين|ماسك|ماسكا|يمسكون|يمسك الشي|يمسك شي");
        AR("pick_up", "يلقط|لقط|التقط|يلتقط|يشيل|شيل|يرفع|رفع|ياخذ|اخذ|يجمع|جمع|ينحني ويلتقط|ينحني ويرفع|ينحني ويشيل|يوطي ويشيل|يوطي ويلقط|يوطي ويرفع|يقرفص ويشيل|يلتقط الشي|يلتقط الشيء|يشيل من الارض|يرفع من الارض|يلتقط من الارض|يلقط من الارض|ياخذ من الارض|يشيل الشي|يرفع الشي|يشيل شي|يشيل الشيء|يرفع شي");
        AR("carry", "يحمل|حمل|حامل|يحملون|يشيل شي ثقيل|يشيل صندوق|يشيل شنطه|يشيل كيس|يشيل حمل ثقيل|يشيل علي كتفه|يشيل علي ظهره|يحمل صندوق|يحمل شنطه|يحمل كيس|يحمل علي كتفه|يحمل علي ظهره|يحمل شي ثقيل|يحمل الصندوق|يحمل جثه|يحمل جريح|يحمل طفل|يحمل شخص|يحمل بين يديه|يحمل بين ذراعيه|يحمل بكلتا يديه");
        AR("push", "يدفع|دفع|دفعه|دفعات|يزق|زق|زقه|يدفش|دفش|دفشه|يدز|دز|دزه|يدفر|دفر|دفره|يدفع الباب|يدفع الحائط|يدفع الجدار|يدفع الصندوق|يدفع الكرسي|يدفع الشخص|يدفع بيديه|يدفع بكلتا يديه|يدفع باليدين|يدفع بقوه|يدفع لقدام|يدفع للامام|يدفع بعيدا|يدفعون");
        AR("pull", "يسحب|سحب|سحبه|يجر|جر|جره|يجرجر|جرجر|يشد|شد|شده|يجذب|جذب|جذبه|يسحب الباب|يسحب الحبل|يسحب الصندوق|يسحب الكرسي|يسحب الشخص|يسحب بيديه|يسحب بكلتا يديه|يسحب باليدين|يسحب بقوه|يسحب لورا|يسحب للخلف|يسحب نحوه|يسحب الي نفسه|يسحبون|ساحب|ساحبا");
        AR("roll", "يتدحرج|تدحرج|يتقلب|تقلب|يتشقلب|شقلب|اتشقلب|شقلبه|يشقلب|ينقلب|انقلب|يتلفلف|يتدحرج علي الارض|يتقلب علي الارض|يتدحرج للامام|يتدحرج لقدام|يتدحرج جانبا|يعمل شقلبه|يعمل شقلبات|يقوم بشقلبه|يؤدي شقلبه|يتشقلب للامام|يتشقلب للخلف|حركه بهلوانيه|يعمل حركه بهلوانيه|يتدحرج مرتين");
        AR("aim", "يصوب|تصويب|يصوب سلاحه|يصوب مسدسه|يصوب بندقيته|يصوب البندقيه|يصوب المسدس|يصوب نحو|يصوب علي|يصوب باتجاه|يستهدف|استهدف|يوجه سلاحه|يوجه المسدس|يوجه البندقيه|ينشن|نشن|يرفع سلاحه|يرفع المسدس|يرفع البندقيه|يشهر سلاحه|يشهر المسدس|يشهر البندقيه|يشهر مسدسه|يسحب سلاحه|يسحب مسدسه|يخرج سلاحه|يخرج مسدسه|يمسك سلاحه|يمسك مسدسه|يمسك السلاح|يمسك المسدس|يمسك السلاح بيديه|يمسك المسدس بيديه|يمسك السلاح بكلتا يديه|يشد القوس|يشد قوسه|يشد الوتر|يسحب القوس|يصوب بالقوس|يصوب بالسهم");
        AR("shoot", "يطلق النار|اطلق النار|اطلاق النار|يطلق رصاصه|يطلق الرصاص|يطلق رصاص|يطلق طلقه|يطلق طلقات|يطلق علي|يطلق نحو|يطلق باتجاه|يطخ|طخ|اطخ|طخه|يضرب نار|ضرب نار|يضرب بالرصاص|يضرب رصاص|يضرب رصاصه|يضرب طلقه|يضرب طلقات|يرمي بالرصاص|يرمي رصاص|يرمي رصاصه|يرمي طلقه|يرمي نار|يقنص|قنص|يضغط الزناد|يضغط علي الزناد|يسحب الزناد|يفتح النار|افتح النار|فتح النار|يفتح نار|يفتح رصاص|يطلق سهم|يطلق السهم|يطلق سهما|يطلق قذيفه|يطلق صاروخ|يطلق نيران|يطلق النيران|يطلق نار|يطلق النار علي|يطلق النار نحو|يطلق النار باتجاه|يطلق النار في الهواء|يطلق النار للاعلي|يطلق النار لفوق|يطلق النار عشوائيا|يطلق النار بكثافه|يطلق النار بقوه|يرشق بالرصاص|يرشق بالنار|يرشق رصاص");
        AR("drink", "يشرب|شرب|شربه|يرتشف|ارتشف|ارتشاف|يحتسي|احتسي|احتساء|يعب|عب|يكرع|كرع|يتجرع|تجرع|يشرب ماء|يشرب الماء|يشرب مي|يشرب الميه|يشرب مويه|يشرب قهوه|يشرب القهوه|يشرب شاي|يشرب الشاي|يشرب عصير|يشرب حليب|يشرب كاس|يشرب الكاس|يشرب فنجان|يشرب كوب|يشرب زجاجه|يشرب من الكاس|يشرب من الفنجان|يشرب من الكوب|يشرب من الزجاجه|يشرب جرعه|يشرب رشفه|يشربون|شارب|شاربا");
        AR("eat", "ياكل|اكل|ياكلون|ياكلها|ياكله|ياچل|يتناول|تناول|يلتهم|التهم|التهام|يقضم|قضم|قضمه|يمضغ|مضغ|مضغه|يتغدي|تغدي|يتعشي|تعشي|يفطر|فطر|فطور|يتفطر|يتذوق|تذوق|يبلع|بلع|يتسحر|تسحر|سحور|يتعشى|يتغدى|يتناول الطعام|يتناول طعام|يتناول الغداء|يتناول العشاء|يتناول الفطور|يتناول وجبه|ياكل طعام|ياكل الطعام|ياكل غداء|ياكل الغداء|ياكل عشاء|ياكل العشاء|ياكل فطور|ياكل وجبه|ياكل تفاحه|ياكل موز|ياكل خبز|ياكل لحم|ياكل رز|ياكل بسرعه|ياكل ببطء|ياكل بشراهه|ياكل بنهم|ياكل بيديه|ياكل بيده|ياكل بالملعقه|ياكل بالشوكه|ياكل بالاصابع|ياكل ويشرب|الاكل|طعام|الطعام|غداء|الغداء|عشاء|العشاء|وجبه|الوجبه");
        AR("yawn", "يتثاءب|يتثاوب|تثاءب|تثاوب|تثاؤب|يتثاءب بصوت عالي|يتثاوب بصوت عالي|يتثاءب بتعب|يتثاوب بتعب|يتثاءب من التعب|يتثاوب من التعب|يتثاءب من النعاس|يتثاوب من النعاس|يتثاءب من الملل|يتثاوب من الملل|يتثاءب ويتمطي|يتثاوب ويتمطي|يتمطي ويتثاءب|يتمطي ويتثاوب");
        AR("stretch", "يتمطي|تمطي|يتمطط|تمطط|يتمطع|تمطع|يمدد ذراعيه|يمدد يديه|يمدد ايديه|يمدد جسمه|يمدد جسده|يمدد ظهره|يمدد رجليه|يمدد عضلاته|يفرد ذراعيه|يفرد يديه|يفرد ايديه|يفرد جسمه|يفرد ظهره|يرفع ذراعيه فوق راسه|يرفع يديه فوق راسه|يرفع ايديه فوق راسه|يسخن|سخن|اسخن|تسخين|يسخن عضلاته|يسخن جسمه|يمارس تمارين الاطاله|يمارس الاطاله|يمارس تمارين التسخين|يمارس التسخين|يعمل تمارين الاطاله|يعمل الاطاله|يعمل التسخين|يؤدي تمارين الاطاله|يؤدي الاطاله|يؤدي التسخين|يقوم بتمارين الاطاله|يقوم بالاطاله|يقوم بالتسخين|اطاله|تمارين الاطاله|تمارين التسخين|تسخين العضلات|اطاله العضلات");
        AR("nod", "يومئ|اومئ|اومي|يومي|يوميء|ايماء|ايماءه|يهز راسه|يهز رأسه|يهز دماغه|يهز راسه موافقا|يهز راسه بنعم|يهز راسه للامام|يهز راسه لتحت|يهز راسه تاييدا|يهز راسه فهما|يوافق|وافق|اوافق|موافقه|يومئ براسه|يومئ برأسه|يومئ موافقا|يومئ بنعم|يحرك راسه موافقا|يحني راسه موافقا|يهز براسه|يهز برأسه|هز راسه|هز رأسه|يهز راسه مرتين|يهز راسه ببطء|يهز راسه بهدوء|يهز راسه بتفهم|يهز راسه مبتسما");
        AR("shake_head", "يرفض|ارفض|رفض|رفضا|يهز راسه رفضا|يهز راسه بلا|يهز راسه يمين ويسار|يهز راسه يمينا وشمالا|يهز راسه يمين وشمال|يهز راسه افقيا|يهز راسه نفيا|يهز راسه معترضا|يهز راسه اعتراضا|يحرك راسه رفضا|يحرك راسه بلا|يحرك راسه يمين ويسار|ينفي|نفي|انفي|نفيا|يعترض|اعترض|اعتراض|لا يوافق|لا يقبل|يقول لا|يقول كلا|ينفي براسه|يرفض براسه|يعترض براسه|يهز رأسه رفضا|يهز رأسه بلا|يهز رأسه نفيا|يهز رأسه يمين ويسار|يهز راسه يمنه ويسره");
        AR("shrug", "يهز كتفيه|يهز كتفه|يهز اكتافه|يرفع كتفيه|يرفع كتفه|يرفع اكتافه|يحرك كتفيه|هز كتفيه|هز كتفه|رفع كتفيه|يهز الكتفين|يهز الكتف|يرفع الكتفين|يهز كتفيه لا ادري|يهز كتفيه مدري|يهز كتفيه بلا مبالاه|يهز كتفيه ببرود|يهز كتفيه استسلاما|يهز كتفيه حيره");
        AR("hug", "يحضن|حضن|حضنه|حضنات|يعانق|عانق|عناق|معانقه|يتعانق|تعانق|يتعانقون|يحتضن|احتضن|احتضان|يضم|ضم|ضمه|يضمه|يضمها|يضمهم|يضمك|يضمني|يضمنا|يحضنه|يحضنها|يحضنهم|يحضنك|يحضنني|يحضننا|يعانقه|يعانقها|يعانقهم|يعانقك|يعانقني|يعانقنا|يحتضنه|يحتضنها|يحتضنهم|يضم صديقه|يضم اخاه|يضم امه|يحضن صديقه|يحضن امه|يعانق صديقه|يعانق امه|يحضن بقوه|يحضن بحب|يعانق بقوه|يعانق بحب|يحضن ويبكي|يبكي ويحضن|يحضن ويضحك");

        // Short imperatives (and other forms) that cannot be derived from "يفعل".
        foreach (var (a, forms) in new (string, string)[]
        {
            ("stand_up", "قوم|قومي|قومو"), ("jump", "نط|انط|تنط|نطي|نطو"), ("fall", "اقع|تقع|طيح|اطيح|تطيح|وقعو"), ("turn_around", "لف|تلف|دور|تدور|لفي|لفو"),
            ("stop", "قف|وقف|توقف|اوقف"), ("look", "بص|شوف|طالع|عاين|ناظر|انظر|بصو"), ("run", "اجري|اجر|اركض|اهرب|اشرد|اعدو|ركض|جري|اجروا"),
            ("walk", "امشي|مشي|سير|سيري|تمشي|اتمشي|امشو|امشوا"), ("die", "موت|اموت|تموت|مات"), ("sit", "اقعد|اجلس|قعد|جلس|اجلسي|اقعدي"),
            ("kick", "اركل|ارفس|ركل|رفس|شوت"), ("punch", "اضرب|الكم|ضرب|لكم|اهجم"), ("wave", "لوح|الوح|اشر|شاور"), ("dance", "ارقص|رقص"),
            ("throw", "ارمي|ارمو|احدف|اقذف|رمي"), ("pick_up", "شيل|اشيل|ارفع|القط|اشيلها"), ("crouch", "وطي|قرفص|دنقر|انحني"), ("step_back", "ارجع|رجع|ترجع|تراجع|ابتعد"),
            ("dodge", "تفادي|راوغ|اتفادي|زوغ"), ("roll", "تدحرج|اتدحرج|شقلب|اتشقلب"), ("climb", "تسلق|اتسلق|اصعد|اطلع"), ("sleep", "نام|انام"), ("eat", "كلي|اكل"),
            ("drink", "اشرب|شرب"), ("clap", "صفق|اصفق|صقف|سقف"), ("cry", "ابكي|بكي|اعيط"), ("laugh", "اضحك|ضحك"), ("hug", "احضن|عانق"), ("push", "ادفع|زق|دفع"), ("pull", "اسحب|اشد|سحب"),
            ("shout", "اصرخ|صيح|نادي"), ("think", "فكر|افكر"), ("block", "صد|اصد|دافع"), ("shoot", "اطلق النار|اطخ|طخ"),
        })
            foreach (var w in forms.Split('|')) Put(Normalize(w), new Entry(K.Action, a), w, true);
        // phone and weapon nouns are context words (cues): «يطالع جواله» must not split, «يتكلم بالجوال» means a phone call.
        foreach (var n in new[] { "تلفون", "جوال", "هاتف", "موبايل", "موبيل", "تليفون", "تلفونه", "جواله", "هاتفه", "موبايله", "تليفونه", "هاتفيا", "تلفونيا" })
            Put(Normalize(n), new Entry(K.Cue, "phone_call"), n, false);
        foreach (var n in new[] { "سلاح", "سلاحه", "مسدس", "مسدسه", "بندقيه", "بندقيته", "رشاش", "رشاشه" })
            Put(Normalize(n), new Entry(K.Cue, "aim"), n, false);
    }
}
