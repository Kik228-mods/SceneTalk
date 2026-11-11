using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MelonLoader;
using MelonLoader.Utils;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Il2CppInterop.Runtime;
using Il2CppTMPro;

[assembly: MelonInfo(typeof(SceneTalk.Mod), "SceneTalk", "0.3.1", "claude")]

namespace SceneTalk
{
    // Talk during a scene: a "Say..." button in the scene button list; replies depend on the personality module,
    // sympathy and lust, and memory of the previous scene. Lines: UserData/SceneTalk/lines_en.txt (lang=en) or lines.txt (lang=ru).
    public class Mod : MelonMod
    {
        static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "SceneTalk");
        static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static readonly string[] Cats = { "praise", "tender", "lewd", "command", "rude", "ask" };
        static readonly string[] CatNamesRu = { "Похвалить", "Нежно", "Пошло", "Приказать", "Грубо", "Спросить, как ей" };
        static readonly string[] CatNamesEn = { "Praise", "Tender", "Lewd", "Command", "Rude", "Ask how she feels" };
        static string Lang = "en";
        static bool En => Lang == "en";
        static string BranchGuid = "";
        static string[] CatNames => En ? CatNamesEn : CatNamesRu;
        static string SayLabel => En ? "Say..." : "Сказать...";
        static void ReadLang()
        {
            Lang = "en"; BranchGuid = "";
            try { var c = Path.Combine(Dir, "config.txt"); if (File.Exists(c)) foreach (var r in File.ReadAllLines(c)) { var t = r.Trim(); if (t.StartsWith("lang=")) Lang = t.Substring(5).Trim().ToLowerInvariant(); if (t.StartsWith("guid=")) BranchGuid = t.Substring(5).Trim(); } } catch { }
            if (string.IsNullOrEmpty(BranchGuid)) BranchGuid = AutoGuid(GameLang());   // must match the game language, not the lines
            L("reply text source guid: " + (string.IsNullOrEmpty(BranchGuid) ? "NONE FOUND" : BranchGuid));
        }
        // The game looks branch text up in the translation table of the mod whose guid we pass,
        // so we pick an installed mod whose language matches the game language (no languageCode = English).
        static string AutoGuid(string lang)
        {
            try
            {
                string root = null;
                try { root = UnityEngine.Application.persistentDataPath; } catch { }
                if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "IncontinentCell", "My Dystopian Robot Girlfriend");
                var modsDir = Path.Combine(root, "Mods");
                if (!Directory.Exists(modsDir)) { L("game mods folder not found: " + modsDir); return ""; }
                foreach (var f in Directory.GetFiles(modsDir, "*.zip").OrderBy(x => x))
                {
                    try
                    {
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                        {
                            var e = za.GetEntry("mod.json"); if (e == null) continue;
                            string txt; using (var sr = new StreamReader(e.Open(), Encoding.UTF8)) txt = sr.ReadToEnd();
                            var lm = Regex.Match(txt, "\"languageCode\"\\s*:\\s*\"([^\"]*)\"");
                            var code = lm.Success ? lm.Groups[1].Value.Trim().ToLowerInvariant() : "";
                            bool ok = lang == "en" ? (code == "" || code == "en") : code == lang;
                            if (!ok) continue;
                            var gm = Regex.Match(txt, "\"serializedGuid\"\\s*:\\s*\"([0-9a-fA-F\\-]{36})\"");
                            if (gm.Success) { L("using mod for reply text: " + Path.GetFileName(f)); return gm.Groups[1].Value; }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e) { L("auto guid: " + e.Message); }
            return "";
        }
        static readonly System.Random rnd = new System.Random();

        static Assembly asm;
        static bool inScene, sessionOn;
        static DateTime sceneEndedAt = DateTime.MinValue, busyUntil = DateTime.MinValue, nextEnsure = DateTime.MinValue, memoryAt = DateTime.MaxValue;
        static GameObject mainBtn, overlay, popup;
        static TMP_FontAsset font; static float fontSize = 30f;
        static readonly Color ButtonCol = new Color(0.114f, 0.027f, 0.204f, 1f);
        static readonly Color HoverCol = new Color(0.67f, 0.05f, 0.31f, 1f);
        static readonly Color PressCol = new Color(0.55f, 0.04f, 0.25f, 1f);
        static readonly Color WindowCol = new Color(0.137f, 0.071f, 0.286f, 0.97f);
        static readonly Color DarkCol = new Color(0.106f, 0.012f, 0.18f, 1f);
        static Dictionary<string, List<string>> lines = new Dictionary<string, List<string>>();
        static readonly Dictionary<string, int> lastPick = new Dictionary<string, int>();
        static readonly Dictionary<string, int> sceneCount = new Dictionary<string, int>();
        static int rudeStreak, idSeq = 7000;

        public static void L(string s) { MelonLogger.Msg(s); }

        // ---------- start ----------
        public override void OnLateInitializeMelon()
        {
            Directory.CreateDirectory(Dir);
            asm = typeof(Il2Cpp.PopupChoice).Assembly;
            var fs = asm.GetTypes().First(t => t.Name == "FrickScene");
            foreach (var m in fs.GetMethods(All).Where(m => m.Name == "StartNewView")) HarmonyInstance.Patch(m, postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(SceneOn))));
            foreach (var m in fs.GetMethods(All).Where(m => m.Name == "EndOldView")) HarmonyInstance.Patch(m, prefix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(SceneOff))));
            // while our reply is playing, vanilla scene lines do not interrupt it
            foreach (var t in asm.GetTypes())
            {
                bool isTarget = false; for (var b = t; b != null; b = b.BaseType) if (b.Name == "StoryBranchTarget") { isTarget = true; break; }
                if (!isTarget) continue;
                foreach (var m in t.GetMethods(All).Where(m => m.Name == "Activate" && !m.IsAbstract))
                    try { HarmonyInstance.Patch(m, prefix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(Guard)))); } catch (Exception e) { L("guard " + t.Name + ": " + e.Message); }
            }
            foreach (var t in asm.GetTypes())
            {
                if (t.Name != "FrickStateButtonSource") continue;   // present in every sex scene; the blowjob/cowgirl sources share its list
                var m = t.GetMethod("AddButtonsToList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (m == null) continue;
                try { HarmonyInstance.Patch(m, postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(AddOurButton)))); L("button source hooked: " + t.Name); }
                catch (Exception e) { L("hook " + t.Name + ": " + e.Message); }
            }
            LoadLines();
            L("ready, lines loaded: " + lines.Values.Sum(v => v.Count));
        }

        public static void SceneOn()
        {
            inScene = true;
            if (!sessionOn)       // new scene (not a pose change)
            {
                sessionOn = true; moduleLogged = false; sceneCount.Clear(); rudeStreak = 0; LoadLines();
                memoryAt = DateTime.Now.AddSeconds(4);
            }
        }

        static bool ours; static DateTime protectUntil = DateTime.MinValue;
        public static bool Guard(object[] __args)
        {
            if (ours || !inScene || DateTime.Now >= protectUntil) return true;
            try { foreach (var a in __args ?? new object[0]) { var act = a as Il2CppSystem.Action; if (act != null) act.Invoke(); } } catch { }
            return false;
        }

        public static void SceneOff() { inScene = false; sceneEndedAt = DateTime.Now; }   // button is removed in OnUpdate if no new view starts (pose change keeps it)

        public override void OnUpdate()
        {
            if (sessionOn && !inScene && (DateTime.Now - sceneEndedAt).TotalSeconds > 5) { sessionOn = false; SaveMemory(); }
            if (!inScene) { if (mainBtn != null && (DateTime.Now - sceneEndedAt).TotalSeconds > 0.6) DestroyButtons(); return; }
            if (DateTime.Now >= memoryAt) { memoryAt = DateTime.MaxValue; try { MemoryLine(); } catch (Exception e) { L("memory: " + e.Message); } }
            if (NativeActive()) { if (mainBtn != null) DestroyButtons(); return; }
            try { Mirror(); } catch { }
            // cheap per-frame check; full scan every frame while the button is missing (pose change), otherwise once a second
            bool alive = false; try { alive = mainBtn != null && mainBtn.transform.parent != null && mainBtn.activeInHierarchy; } catch { }
            if (alive) { try { var tr = mainBtn.transform; if (overlay == null || tr.parent != overlay.transform) { if (tr.GetSiblingIndex() != tr.parent.childCount - 1) tr.SetAsLastSibling(); } } catch { } }
            if (!alive || DateTime.Now >= nextEnsure) { nextEnsure = DateTime.Now.AddSeconds(alive ? 1.0 : 0.03); try { EnsureButtons(); } catch (Exception e) { L("buttons: " + e.Message); } }
        }

        // ---------- lines ----------
        static void LoadLines()
        {
            ReadLang();
            var f = Path.Combine(Dir, En ? "lines_en.txt" : "lines.txt");
            var d = new Dictionary<string, List<string>>();
            if (!File.Exists(f)) { L("no file " + f); lines = d; return; }
            List<string> cur = null;
            foreach (var raw in File.ReadAllLines(f, Encoding.UTF8))
            {
                var s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                if (s.StartsWith("[") && s.EndsWith("]")) { var k = s.Substring(1, s.Length - 2).Trim(); if (!d.TryGetValue(k, out cur)) { cur = new List<string>(); d[k] = cur; } continue; }
                cur?.Add(s);
            }
            lines = d;
        }

        static string Pick(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!lines.TryGetValue(k, out var arr) || arr.Count == 0) continue;
                int i = rnd.Next(arr.Count);
                if (arr.Count > 1 && lastPick.TryGetValue(k, out var last) && last == i) i = (i + 1 + rnd.Next(arr.Count - 1)) % arr.Count;
                lastPick[k] = i; return arr[i];
            }
            return null;
        }

        // ---------- bot state ----------
        static object commonConds;
        static readonly HashSet<string> condWarned = new HashSet<string>();
        static bool Cond(string name)
        {
            try
            {
                if (commonConds == null)
                {
                    foreach (var t in asm.GetTypes().Where(x => x.Name == "StoryCondition"))
                    {
                        var p = t.GetProperty("CommonConditions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (p != null) { commonConds = p.GetValue(null); break; }
                        var f = t.GetField("CommonConditions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (f != null) { commonConds = f.GetValue(null); break; }
                    }
                }
                var ct = commonConds.GetType();
                object c = ct.GetProperty(name)?.GetValue(commonConds) ?? ct.GetField(name)?.GetValue(commonConds);
                if (c == null) { if (condWarned.Add(name)) L("no condition " + name); return false; }
                var m = c.GetType().GetMethod("ConditionMet", Type.EmptyTypes);
                return (bool)m.Invoke(c, null);
            }
            catch (Exception e) { if (condWarned.Add(name)) L("condition " + name + ": " + (e.InnerException?.Message ?? e.Message)); return false; }
        }

        static string Tier()
        {
            bool high = Cond("SympathyLoveOnly") || Cond("SympathySuperLoveOnly");
            if (high) return (Cond("HighLust") || Cond("VeryHighLust") || Cond("ExtremelyLust")) ? "hot" : "high";
            if (Cond("SympathyAffectionOnly") || Cond("SympathyLikeOnly")) return "mid";
            return "low";
        }

        static bool moduleLogged;
        static string Module()
        {
            try
            {
                if (!moduleLogged)
                {
                    moduleLogged = true;
                    var mu0 = Inst(typeof(Il2Cpp.ModUtilities)) as Il2Cpp.ModUtilities; var l0 = mu0.GetGameVariables().itemManager.botEquippedItems;
                    var sb = new StringBuilder();
                    for (int i = 0; i < l0.Count; i++) sb.Append(l0[i].EquippedSlot + "=" + (l0[i].GetItemPrefab()?.Name ?? "?") + "; ");
                    L("equipped: " + sb);
                    L("sympathy: like=" + Cond("SympathyLikeOnly") + " affection=" + Cond("SympathyAffectionOnly") + " love=" + Cond("SympathyLoveOnly") + " superlove=" + Cond("SympathySuperLoveOnly") + " lust: high=" + Cond("HighLust") + " veryhigh=" + Cond("VeryHighLust") + " extreme=" + Cond("ExtremelyLust"));
                }
                var mu = Inst(typeof(Il2Cpp.ModUtilities)) as Il2Cpp.ModUtilities;
                var l = mu.GetGameVariables().itemManager.botEquippedItems;
                for (int i = 0; i < l.Count; i++)
                {
                    if (l[i].EquippedSlot != "PersonalityModule") continue;
                    var n = l[i].GetItemPrefab()?.Name ?? "";
                    if (n.Contains("Alaya")) return "alaya";
                    var low = n.ToLowerInvariant().Replace(" ", "").Replace("-", "");
                    // vanilla modules (English item names, Russian as a guess for a Russian game)
                    if (low.Contains("tsuntsun") || low.Contains("цун")) return "tsuntsun";
                    if (low.Contains("sillycat") || low.Contains("глупаякош")) return "sillycat";
                    if (low.Contains("catgirl") || low.Contains("кошкодев")) return "catgirl";
                    if (low.Contains("driver") || low.Contains("водител") || low.Contains("гонщ")) return "driver";
                    if (low.Contains("robotpersonality") || low.Contains("робот")) return "robot";
                    if (low.Contains("yandere") || low.Contains("яндере")) return "yandere";
                    if (low.Contains("slutpersonality") || low.Contains("шлюх")) return "slut";
                    if (low.Contains("femcel") || low.Contains("фемцел")) return "femcel";
                    if (low.Contains("himedere") || low.Contains("химедере")) return "himedere";
                    if (low.Contains("araara") || low.Contains("араара")) return "araara";
                }
            }
            catch { }
            return "default";
        }

        static object Inst(Type t)
        {
            for (var b = t; b != null; b = b.BaseType) { var p = b.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static); if (p != null) return p.GetValue(null); }
            return null;
        }

        // ---------- talk ----------
        static string GameLang()
        {
            try
            {
                var lt = asm?.GetTypes().FirstOrDefault(t => t.Name == "LocalizationSingleton"); if (lt == null) return "en";
                var ls = Inst(lt); if (ls == null) return "en";
                var anyF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var ci = ls.GetType().GetProperty("CurrentLanguageCultureInfo", anyF)?.GetValue(ls); if (ci == null) return "en";
                var s = ci.GetType().GetProperty("TwoLetterISOLanguageName", anyF)?.GetValue(ci) as string;
                return string.IsNullOrEmpty(s) ? "en" : s.ToLowerInvariant();
            }
            catch { return "en"; }
        }
        // LivelyJun (optional): shares Lin's mood; found by reflection, no hard dependency
        static Type llApi; static bool llLooked;
        static Type LLApi()
        {
            if (!llLooked) { llLooked = true; foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = null; try { t = a.GetType("LivelyJun.Api"); } catch { } if (t != null) { llApi = t; L("LivelyJun found: her mood is shared"); break; } } }
            return llApi;
        }
        static object LLGet(string p) { try { return LLApi()?.GetProperty(p)?.GetValue(null); } catch { return null; } }
        static void LLReport(string e) { try { LLApi()?.GetMethod("Report")?.Invoke(null, new object[] { e }); } catch { } }

        static void Talk(int ci)
        {
            if (DateTime.Now < busyUntil) return;
            var cat = Cats[ci]; var mod = Module(); var tier = Tier();
            var llt = LLGet("Tier") as string;          // her own mood caps how warm the reply can be
            if (llt == "cold") tier = "low"; else if (llt == "cool" && (tier == "high" || tier == "hot")) tier = "mid";
            LLReport("scene." + cat);
            sceneCount[cat] = (sceneCount.TryGetValue(cat, out var c) ? c : 0) + 1;
            rudeStreak = cat == "rude" ? rudeStreak + 1 : 0;
            var me = Pick("player." + cat) ?? CatNames[ci];
            string bot = null;
            if (rudeStreak >= 3) { bot = Pick(mod + ".streak.rude", "default.streak.rude"); rudeStreak = 0; }
            if (bot == null)
            {
                var order = tier == "hot" ? new[] { "hot", "high", "mid", "low" } : tier == "high" ? new[] { "high", "mid", "low" } : tier == "mid" ? new[] { "mid", "low", "high" } : new[] { "low", "mid" };
                var keys = new List<string>();
                foreach (var t in order) keys.Add(mod + "." + cat + "." + t);
                foreach (var t in order) keys.Add("default." + cat + "." + t);
                bot = Pick(keys.ToArray()) ?? "...";
            }
            L("[" + mod + "/" + tier + "] " + cat + ": " + me + " -> " + bot);
            // " | " in a reply = several lines in a row
            Say("Anon: " + me + "\n" + string.Join("\n", bot.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries).Select(x => "Bot: " + x.Trim())));
        }

        static void MemoryLine()
        {
            var mem = ReadMemory(); int total = mem.Values.Sum();
            if (total < 2 || rnd.NextDouble() > 0.45) return;
            int rude = Get(mem, "rude"), sweet = Get(mem, "tender") + Get(mem, "praise"), lewd = Get(mem, "lewd") + Get(mem, "command");
            string kind = rude >= sweet && rude >= lewd ? "rude" : sweet >= lewd ? "sweet" : "lewd";
            var mod = Module(); var bot = Pick(mod + ".memory." + kind, "default.memory." + kind);
            if (bot != null) { L("memory (" + kind + "): " + bot); Say(string.Join("\n", bot.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries).Select(x => "Bot: " + x.Trim()))); }
        }

        static int Depth(Type t) { int d = 0; for (var b = t; b != null; b = b.BaseType) d++; return d; }

        static void Say(string text)
        {
            if (string.IsNullOrEmpty(BranchGuid)) { L("no installed mod found to take reply text from; set guid= in UserData/SceneTalk/config.txt"); return; }
            try
            {
                var branchT = asm.GetTypes().First(t => t.Name == "StoryBotDialogueBranch");
                var ctor = branchT.GetConstructors().First(k => k.GetParameters().Length == 4 && k.GetParameters()[0].ParameterType == typeof(string));
                var guid = Il2CppSystem.Guid.Parse(BranchGuid);
                var branch = ctor.Invoke(new object[] { text, guid, idSeq++, false });
                var target = branchT.GetMethod("ActivateGetTarget").Invoke(branch, null);
                // Activate(Action) exists on the abstract StoryBranchTarget and on its subclasses; reflection order is not
                // guaranteed, so try the most-derived declaration first and fall back to the others.
                // the wrapper may come back typed as the abstract base; cast it to the real runtime subclass
                try
                {
                    var ob = target as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    if (ob != null) L("target runtime class: " + System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(IL2CPP.il2cpp_object_get_class(ob.Pointer))) + " (wrapper " + target.GetType().Name + ")");
                    var tc = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast");
                    foreach (var st in asm.GetTypes().Where(t => !t.IsAbstract && Depth(t) > 0).OrderByDescending(t => Depth(t)))
                    {
                        bool sub = false; for (var b = st.BaseType; b != null; b = b.BaseType) if (b.Name == "StoryBranchTarget") { sub = true; break; }
                        if (!sub) continue;
                        object casted = null;
                        try { casted = tc.MakeGenericMethod(st).Invoke(target, null); } catch { }
                        if (casted != null) { target = casted; L("target cast to " + st.Name); break; }
                    }
                }
                catch (Exception ce) { L("target cast skipped: " + ce.Message); }
                var acts = target.GetType().GetMethods().Where(m => m.Name == "Activate" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Il2CppSystem.Action))
                    .OrderByDescending(m => Depth(m.DeclaringType)).ToList();
                if (acts.Count == 0) throw new Exception("no Activate(Action) found on " + target.GetType().Name);
                busyUntil = DateTime.Now.AddSeconds(12); protectUntil = DateTime.Now.AddSeconds(10);
                ours = true;
                try
                {
                    Exception last = null; bool done = false;
                    foreach (var act in acts)
                    {
                        try { act.Invoke(target, new object[] { DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => { busyUntil = DateTime.Now.AddSeconds(0.3); protectUntil = DateTime.Now.AddSeconds(1.5); })) }); done = true; break; }
                        catch (Exception ex) { last = ex; L("Activate via " + act.DeclaringType.Name + " failed: " + (ex.InnerException?.Message ?? ex.Message)); }
                    }
                    if (!done && last != null) throw last;
                }
                finally { ours = false; }
            }
            catch (Exception e) { L("Say failed: " + (e.InnerException?.Message ?? e.Message)); busyUntil = DateTime.MinValue; }
        }

        // ---------- memory ----------
        static string MemFile => Path.Combine(Dir, "memory.txt");
        static int Get(Dictionary<string, int> d, string k) => d.TryGetValue(k, out var v) ? v : 0;
        static Dictionary<string, int> ReadMemory()
        {
            var d = new Dictionary<string, int>();
            try { foreach (var s in File.ReadAllLines(MemFile)) { var p = s.Split('='); if (p.Length == 2 && int.TryParse(p[1], out var v)) d[p[0].Trim()] = v; } } catch { }
            return d;
        }
        static void SaveMemory()
        {
            if (sceneCount.Count == 0) return;     // nobody talked in this scene - keep the previous one
            try { File.WriteAllLines(MemFile, sceneCount.Select(kv => kv.Key + "=" + kv.Value)); L("saved scene: " + string.Join(", ", sceneCount.Select(kv => kv.Key + "=" + kv.Value))); } catch { }
        }

        // ---------- the "Say..." button ----------
        static IEnumerable<Transform> Lists()
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2Cpp.FrickStaticGui>()))
            {
                var g = o.Cast<Il2Cpp.FrickStaticGui>();
                Il2Cpp.ButtonList l = null, r = null;
                try { l = g.LeftButtonList; } catch { }
                try { r = g.RightButtonList; } catch { }
                if (l != null) yield return l.transform;
                if (r != null) yield return r.transform;
            }
        }

        static GameObject Template(Transform list)
        {
            for (int i = 0; i < list.childCount; i++)
            {
                var ch = list.GetChild(i).gameObject;
                if (ch == mainBtn || !ch.activeInHierarchy) continue;
                if (ch.GetComponent<Image>() != null && ch.GetComponentInChildren<TMP_Text>(true) != null) return ch;
            }
            return null;
        }

        static void EnsureButtons()
        {
            Transform left = null, any = null; GameObject tpl = null;
            int k = 0;
            foreach (var t in Lists())
            {
                var tp = Template(t);
                if (k == 0 && t.gameObject.activeInHierarchy && tp != null) { left = t; tpl = tp; }
                if (tpl == null && tp != null) { tpl = tp; any = t; }
                k++;
            }
            if (tpl != null && font == null) { var tt = tpl.GetComponentInChildren<TMP_Text>(true); font = tt.font; fontSize = tt.fontSize > 8 ? tt.fontSize : 30f; }
            if (left != null)
            {
                if (mainBtn != null && mainBtn.transform.parent == left) return;
                DestroyButtons();
                mainBtn = MakeButton(left, tpl, SayLabel, TogglePopup);
                L("Say button in the scene list");
                return;
            }
            // scene has no left list (blowjob): own button at bottom left
            if (mainBtn != null && overlay != null && mainBtn.transform.parent == overlay.transform) return;
            if (tpl == null) return;
            DestroyButtons();
            EnsureOverlay();
            mainBtn = MakeButton(overlay.transform, tpl, SayLabel, TogglePopup);
            var r = mainBtn.GetComponent<RectTransform>(); var tr = tpl.GetComponent<RectTransform>();
            var size = tr != null ? tr.rect.size : new Vector2(200, 50); if (size.x < 50) size = new Vector2(200, 50);
            r.anchorMin = r.anchorMax = new Vector2(0, 0); r.pivot = new Vector2(0, 0);
            r.sizeDelta = size; r.anchoredPosition = new Vector2(20, 140);
            foreach (var le in mainBtn.GetComponents<LayoutElement>()) le.ignoreLayout = true;
            L("Say button own, bottom left");
        }

        // "Say..." behaves like the neighbouring native button: inactive and transparent when they are
        static void Mirror()
        {
            if (mainBtn == null) return;
            var btn = mainBtn.GetComponent<Button>(); var mt = mainBtn.GetComponentInChildren<TMP_Text>(true);
            var par = mainBtn.transform.parent; Selectable sib = null; TMP_Text st = null;
            if (overlay == null || par != overlay.transform)
                for (int i = 0; i < par.childCount && sib == null; i++)
                {
                    var ch = par.GetChild(i).gameObject; if (ch == mainBtn || !ch.activeInHierarchy) continue;
                    sib = ch.GetComponent<Selectable>() ?? ch.GetComponentInChildren<Selectable>(); st = ch.GetComponentInChildren<TMP_Text>(true);
                }
            bool on = sib == null || sib.interactable;
            if (popup != null) on = true;
            if (btn != null && btn.interactable != on) btn.interactable = on;
            if (mt != null) { var c = mt.color; c.a = st != null ? st.color.a * (st.canvasRenderer != null ? st.canvasRenderer.GetAlpha() : 1f) : 1f; if (!on && st == null) c.a = 0.3f; mt.color = c; }
        }

        static void EnsureOverlay()
        {
            if (overlay != null) return;
            overlay = new GameObject("SceneTalkCanvas");
            UnityEngine.Object.DontDestroyOnLoad(overlay);
            var c = overlay.AddComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 950;
            var sc = overlay.AddComponent<CanvasScaler>(); sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; sc.referenceResolution = new Vector2(1920, 1080); sc.matchWidthOrHeight = 0.5f;
            overlay.AddComponent<GraphicRaycaster>();
        }

        static void DestroyButtons()
        {
            ClosePopup();
            try { if (mainBtn != null) UnityEngine.Object.Destroy(mainBtn); } catch { }
            mainBtn = null;
        }

        static GameObject proto; static Color gameNormal = new Color(0.114f, 0.027f, 0.204f, 0.9f), gameHover = HoverCol, gamePress = PressCol, gameDisabled = new Color(0.114f, 0.027f, 0.204f, 0.25f);
        static void EnsureProto(GameObject tpl)
        {
            if (proto != null || tpl == null) return;
            // real colours of the native button: image colour x state colours
            try
            {
                var img = tpl.GetComponent<Image>(); var baseC = img != null ? img.color : Color.white;
                var sel = tpl.GetComponent<Selectable>();
                if (sel == null) foreach (var x in tpl.GetComponentsInChildren<Selectable>(true)) { sel = x; break; }
                if (sel != null)
                {
                    var cb = sel.colors; float m = cb.colorMultiplier <= 0 ? 1 : cb.colorMultiplier;
                    gameNormal = baseC * cb.normalColor * m; gameHover = baseC * cb.highlightedColor * m; gamePress = baseC * cb.pressedColor * m; gameDisabled = baseC * cb.disabledColor * m;
                    if (gameHover.r + gameHover.g + gameHover.b < gameNormal.r + gameNormal.g + gameNormal.b + 0.05f) gameHover = new Color(HoverCol.r, HoverCol.g, HoverCol.b, gameNormal.a);
                }
                else gameNormal = baseC;
                L("game button colour: " + gameNormal + " hover: " + gameHover);
            }
            catch (Exception e) { L("colour: " + e.Message); }
            var holder = new GameObject("SceneTalkProto"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            proto = UnityEngine.Object.Instantiate(tpl, holder.transform);
            foreach (var a in proto.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(a);
            for (int pass = 0; pass < 3; pass++)
                foreach (var mb in proto.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var n = mb.GetIl2CppType().Name;
                    if (n == "Image" || n == "RawImage" || n == "TextMeshProUGUI" || n == "TMP_SubMeshUI" || n == "Mask" || n == "RectMask2D" || n.Contains("Layout") || n == "ContentSizeFitter") continue;
                    try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
                }
        }

        static void Style(Button btn, Image img)
        {
            img.color = Color.white; img.raycastTarget = true; btn.targetGraphic = img;
            var cb = btn.colors; cb.normalColor = gameNormal; cb.selectedColor = gameNormal; cb.highlightedColor = gameHover; cb.pressedColor = gamePress;
            cb.disabledColor = gameDisabled; cb.colorMultiplier = 1f; cb.fadeDuration = 0.08f; btn.colors = cb;
        }

        static GameObject MakeButton(Transform parent, GameObject tpl, string text, Action onClick)
        {
            EnsureProto(tpl);
            var go = UnityEngine.Object.Instantiate(proto, parent);
            go.name = "SceneTalk_" + text.Trim();
            var tmps = go.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < tmps.Length; i++) { tmps[i].text = i == 0 ? text : ""; tmps[i].color = Color.white; }
            var img = go.GetComponent<Image>();
            var btn = go.AddComponent<Button>(); Style(btn, img);
            btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => { try { onClick(); } catch (Exception e) { L("click: " + e); } })));
            go.SetActive(true);
            go.transform.SetAsLastSibling();
            try { img.canvasRenderer.SetColor(gameNormal); btn.targetGraphic.CrossFadeColor(gameNormal, 0f, true, true); } catch { }
            return go;
        }

        // ---------- popup with options in the centre ----------

        // ---------- the game's own UI: a real button in the scene button list + the game's choice menu ----------
        static Il2Cpp.ButtonListButtonSource nativeSource; static DateTime nativeAt = DateTime.MinValue;
        public static void AddOurButton(Il2Cpp.ButtonListButtonSource __instance, Il2Cpp.IModificationPeriodButtonList __0)
        {
            try
            {
                var w = __0.AddButton(1000, Il2Cpp.CommonButtonColorType.Normal);
                w.SetText(SayLabel);
                w.OnClick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => { try { OpenGameMenu(); } catch (Exception e) { L("menu: " + e.Message); } }));
                nativeSource = __instance; nativeAt = DateTime.Now;
                DestroyButtons();
            }
            catch (Exception e) { L("native button: " + (e.InnerException?.Message ?? e.Message)); }
        }
        static bool NativeActive()
        {
            try { var l = nativeSource?.ConnectedButtonList; return l != null && l.gameObject.activeInHierarchy; } catch { return false; }
        }
        static void OpenGameMenu()
        {
            Il2CppFungus.MenuDialog md = null;
            try { md = Il2CppFungus.MenuDialog.GetMenuDialog(); } catch { }
            if (md == null) { OpenPopup(); return; }
            md.Clear();
            for (int i = 0; i < Cats.Length; i++)
            {
                int ci = i;
                md.AddOption(new Il2CppFungus.MenuOption(CatNames[i], true, DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => { try { Talk(ci); } catch (Exception e) { L("talk: " + e.Message); } }))));
            }
            md.AddOption(new Il2CppFungus.MenuOption(GameText("Interact_Nevermind") ?? (En ? "Nevermind" : "Ничего"), true, DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => { }))));
            md.SetActive(true);
        }
        static string GameText(string prop)
        {
            try { foreach (var t in asm.GetTypes()) { if (t.Name != "UI") continue; var p = t.GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); if (p != null) { var v = p.GetValue(null) as string; return string.IsNullOrEmpty(v) ? null : v; } } } catch { }
            return null;
        }

        static void TogglePopup() { if (popup != null) ClosePopup(); else OpenPopup(); }
        static void ClosePopup()
        {
            if (popup == null) return;
            var p = popup; popup = null;
            try { MelonCoroutines.Start(Fade(p, 1f, 0f, 0.15f, true)); } catch { try { UnityEngine.Object.Destroy(p); } catch { } }
        }

        static System.Collections.IEnumerator Fade(GameObject p, float a0, float a1, float dur, bool destroy)
        {
            CanvasGroup cg = null; try { cg = p.GetComponent<CanvasGroup>() ?? p.AddComponent<CanvasGroup>(); cg.blocksRaycasts = !destroy; } catch { }
            var kids = new List<KeyValuePair<RectTransform, Vector2>>();
            try { foreach (var r in p.GetComponentsInChildren<RectTransform>(true)) if (r.name.StartsWith("SceneTalk_")) kids.Add(new KeyValuePair<RectTransform, Vector2>(r, r.anchoredPosition)); } catch { }
            float t = 0;
            while (t < dur)
            {
                if (p == null) yield break;
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / dur); float e = 1 - (1 - k) * (1 - k);
                float a = Mathf.Lerp(a0, a1, e);
                try { if (cg != null) cg.alpha = a; foreach (var kv in kids) kv.Key.anchoredPosition = kv.Value + new Vector2(0, (1 - a) * -18f); } catch { }
                yield return null;
            }
            try { if (destroy) UnityEngine.Object.Destroy(p); else { if (cg != null) cg.alpha = a1; foreach (var kv in kids) kv.Key.anchoredPosition = kv.Value; } } catch { }
        }

        static RectTransform RT(GameObject g) { var r = g.GetComponent<RectTransform>(); return r != null ? r : g.AddComponent<RectTransform>(); }

        static GameObject Box(Transform parent, string name, Color c)
        {
            var g = new GameObject(name); g.transform.SetParent(parent, false); RT(g);
            var im = g.AddComponent<Image>(); im.color = c; im.raycastTarget = false; return g;
        }

        static TMP_Text Text(Transform parent, string s, float mul)
        {
            var g = new GameObject("Text"); g.transform.SetParent(parent, false); var r = RT(g);
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(12, 0); r.offsetMax = new Vector2(-12, 0);
            var t = g.AddComponent<TextMeshProUGUI>(); if (font != null) t.font = font;
            t.fontSize = fontSize * mul; t.alignment = TextAlignmentOptions.Center; t.color = Color.white; t.raycastTarget = false; t.text = s; return t;
        }

        static void OpenPopup()
        {
            if (proto == null) return;
            EnsureOverlay(); ClosePopup();
            const float BandW = 300f, BW = 650f, BH = 50f, Gap = 10f;
            popup = new GameObject("SceneTalkPopup"); popup.transform.SetParent(overlay.transform, false);
            var pr = RT(popup); pr.anchorMin = Vector2.zero; pr.anchorMax = Vector2.one; pr.offsetMin = pr.offsetMax = Vector2.zero;
            // click outside - close
            var shade = Box(popup.transform, "Shade", new Color(0, 0, 0, 0.001f)); var sr = RT(shade); sr.anchorMin = Vector2.zero; sr.anchorMax = Vector2.one; sr.offsetMin = sr.offsetMax = Vector2.zero;
            shade.GetComponent<Image>().raycastTarget = true;
            var sb = shade.AddComponent<Button>(); sb.transition = Selectable.Transition.None;
            sb.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(ClosePopup)));
            // semi-transparent full-height strip, like the game's choice windows
            var band = Box(popup.transform, "Band", new Color(0.24f, 0.15f, 0.46f, 0.62f)); var br = RT(band);
            br.anchorMin = new Vector2(0.5f, 0); br.anchorMax = new Vector2(0.5f, 1); br.pivot = new Vector2(0.5f, 0.5f); br.sizeDelta = new Vector2(BandW, 0); br.anchoredPosition = Vector2.zero;
            var names = CatNames.Concat(new[] { En ? "Never mind" : "Ничего" }).ToArray();
            float total = names.Length * BH + (names.Length - 1) * Gap;
            for (int i = 0; i < names.Length; i++)
            {
                int ci = i;
                var b = MakeButton(popup.transform, proto, names[i], () => { ClosePopup(); if (ci < Cats.Length) Talk(ci); });
                foreach (var le in b.GetComponents<LayoutElement>()) le.ignoreLayout = true;
                var r = RT(b); r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f); r.pivot = new Vector2(0.5f, 0.5f);
                r.sizeDelta = new Vector2(BW, BH); r.anchoredPosition = new Vector2(0, total / 2 - BH / 2 - i * (BH + Gap));
            }
            var cg0 = popup.AddComponent<CanvasGroup>(); cg0.alpha = 0f;
            MelonCoroutines.Start(Fade(popup, 0f, 1f, 0.2f, false));
        }
    }
}
