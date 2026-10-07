using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoTranslator.IL2CPP.BruteForceFix;

[BepInPlugin(GUID, GUID, Version)]
[BepInDependency("gravydevsupreme.xunity.autotranslator")]
public class AutoTranslatorBruteForceFixPlugin : BasePlugin
{
    public const string GUID = "AutoTranslator.IL2CPP.BruteForceFix";
    public const string Version = "1.0.5";

    internal static ManualLogSource PluginLog;
    internal static string TableFolder;
    internal static DateTime TableStamp;
    internal static float NextStampCheck;

    public override void Load()
    {
        PluginLog = Log;
        TableFolder = System.IO.Path.Combine(Paths.BepInExRootPath, "Translation", "zh-CN", "Text");
        var n = StaticTextTable.Load(TableFolder);
        TableStamp = ReadTableStamp(TableFolder);
        Log.LogInfo($"static table {n} exact from {TableFolder}");
        var patched = TmpSetCharArrayHook.Apply(new Harmony(GUID));
        Log.LogInfo($"patched SetCharArray methods: {patched}");
        AddComponent<AutoTranslatorBruteForceFixComponent>();
    }

    internal static DateTime ReadTableStamp(string folder)
    {
        var max = DateTime.MinValue;
        if (!Directory.Exists(folder))
            return max;
        foreach (var path in Directory.GetFiles(folder, "*.txt"))
        {
            var t = File.GetLastWriteTimeUtc(path);
            if (t > max)
                max = t;
        }
        return max;
    }

    internal static void ReloadTableIfChanged()
    {
        if (string.IsNullOrEmpty(TableFolder) || Time.unscaledTime < NextStampCheck)
            return;
        NextStampCheck = Time.unscaledTime + 1f;
        var stamp = ReadTableStamp(TableFolder);
        if (stamp <= TableStamp)
            return;
        TableStamp = stamp;
        var n = StaticTextTable.Load(TableFolder);
        PluginLog?.LogInfo($"static table reload {n}");
    }

    public sealed class AutoTranslatorBruteForceFixComponent : MonoBehaviour
    {
        private void LateUpdate()
        {
            ReloadTableIfChanged();
            foreach (var t in FindObjectsByType<TMP_Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var displayed = ReadDisplayed(t);
                if (string.IsNullOrEmpty(displayed))
                    continue;
                if (StaticTextTable.TryGet(displayed, out var mapped) && mapped != displayed)
                    t.SetText(mapped);
            }
            foreach (var t in FindObjectsByType<Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var displayed = t.text;
                if (string.IsNullOrEmpty(displayed))
                    continue;
                if (StaticTextTable.TryGet(displayed, out var mapped) && mapped != displayed)
                    t.text = mapped;
            }
        }

        private static string ReadDisplayed(TMP_Text t)
        {
            try
            {
                var parsed = t.GetParsedText();
                if (!string.IsNullOrEmpty(parsed))
                    return parsed;
            }
            catch
            {
                // fall through
            }
            if (!string.IsNullOrEmpty(t.text))
                return t.text;
            try
            {
                var info = t.textInfo;
                if (info == null || info.characterCount <= 0)
                    return null;
                var sb = new StringBuilder(info.characterCount);
                for (var i = 0; i < info.characterCount; i++)
                {
                    var ch = info.characterInfo[i];
                    if (ch.isVisible)
                        sb.Append(ch.character);
                }
                return sb.Length == 0 ? null : sb.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}

internal static class TmpSetCharArrayHook
{
    public static int Apply(Harmony harmony)
    {
        var n = 0;
        foreach (var method in typeof(TMP_Text).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != "SetCharArray")
                continue;
            var args = method.GetParameters();
            var prefix = args.Length == 1
                ? nameof(Prefix1)
                : args.Length == 3
                    ? nameof(Prefix3)
                    : null;
            if (prefix == null)
                continue;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(TmpSetCharArrayHook), prefix));
            n++;
        }
        return n;
    }

    private static void Prefix1(ref Il2CppStructArray<char> __0)
    {
        if (__0 == null || __0.Length == 0)
            return;
        var src = Slice(__0, 0, __0.Length);
        if (!StaticTextTable.TryGet(src, out var dst) || dst == src)
            return;
        __0 = ToArray(dst);
    }

    private static void Prefix3(ref Il2CppStructArray<char> __0, ref int __1, ref int __2)
    {
        if (__0 == null || __2 <= 0)
            return;
        var src = Slice(__0, __1, __2);
        if (!StaticTextTable.TryGet(src, out var dst) || dst == src)
            return;
        __0 = ToArray(dst);
        __1 = 0;
        __2 = dst.Length;
    }

    private static string Slice(Il2CppStructArray<char> arr, int start, int length)
    {
        var buf = new char[length];
        for (var i = 0; i < length; i++)
            buf[i] = arr[start + i];
        return new string(buf);
    }

    private static Il2CppStructArray<char> ToArray(string s)
    {
        var arr = new Il2CppStructArray<char>(s.Length);
        for (var i = 0; i < s.Length; i++)
            arr[i] = s[i];
        return arr;
    }
}

internal static class StaticTextTable
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<KeyValuePair<Regex, string>> Regexes = new();
    private static readonly HashSet<string> Unmatched = new();

    public static int Load(string folder)
    {
        Exact.Clear();
        Regexes.Clear();
        if (!Directory.Exists(folder))
            return 0;
        foreach (var path in Directory.GetFiles(folder, "*.txt")
                     .OrderBy(p => System.IO.Path.GetFileName(p).StartsWith("_", StringComparison.Ordinal) ? 0 : 1)
                     .ThenBy(p => System.IO.Path.GetFileName(p), StringComparer.Ordinal))
        {
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (line.StartsWith("r:\"", StringComparison.Ordinal))
                {
                    var end = line.IndexOf("\"=", 3, StringComparison.Ordinal);
                    if (end < 0)
                        continue;
                    var pattern = line.Substring(3, end - 3);
                    var dest = Unescape(line.Substring(end + 2));
                    try
                    {
                        Regexes.Add(new KeyValuePair<Regex, string>(
                            new Regex(pattern, RegexOptions.CultureInvariant),
                            dest));
                    }
                    catch
                    {
                        // skip bad per-game regex
                    }
                    continue;
                }
                var eq = IndexOfUnescapedEquals(line);
                if (eq <= 0)
                    continue;
                var src = Unescape(line.Substring(0, eq));
                var dst = Unescape(line.Substring(eq + 1));
                if (src.Length == 0)
                    continue;
                Exact[src] = dst;
            }
        }
        return Exact.Count;
    }

    public static bool TryGet(string src, out string dst)
    {
        src = StripInvisible(src);
        if (LookupExact(src, out dst))
            return true;
        var folded = src.Replace('♥', '♡');
        if (folded != src && LookupExact(folded, out dst))
            return true;
        var oneline = folded.Replace("\r", "").Replace("\n", "");
        if (oneline != folded && LookupExact(oneline, out dst))
            return true;
        foreach (var pair in Regexes)
        {
            if (!pair.Key.IsMatch(src) && !pair.Key.IsMatch(folded))
                continue;
            var from = pair.Key.IsMatch(src) ? src : folded;
            dst = pair.Key.Replace(from, pair.Value);
            return dst != src;
        }
        if (src.Length >= 10 && TryPrefix(src, out dst))
            return true;
        if (folded.Length >= 10 && folded != src && TryPrefix(folded, out dst))
            return true;
        NoteUnmatched(src);
        dst = null;
        return false;
    }

    private static string StripInvisible(string src)
    {
        if (string.IsNullOrEmpty(src))
            return src;
        return src.Trim('\u180e', '\u200b', '\ufeff', '\u00a0');
    }

    private static void NoteUnmatched(string src)
    {
        if (src == null || src.Length < 2)
            return;
        if (src.Length < 8 && !IsShortVocal(src))
            return;
        if (!HasKana(src) || !Unmatched.Add(src))
            return;
        AutoTranslatorBruteForceFixPlugin.PluginLog?.LogInfo(
            "unmatched " + src.Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    private static bool HasKana(string src)
    {
        foreach (var c in src)
        {
            if (c >= '\u3041' && c <= '\u3096')
                return true;
            if (c >= '\u30A1' && c <= '\u30FA' || c == '\u30FC')
                return true;
        }
        return false;
    }

    // Same moan alphabet as local_qwen._VOCAL_CHAR_RE. Not all hiragana:
    // that would log every typewriter prefix of every line.
    private const string VocalChars =
        "ぁあぃいぅうぇえぉおんっッふはひへほー〜～つ♥♡❤！!？?…。．.⋯" +
        "ァアィイゥウェエォオンフハヒヘホ";

    private static bool IsShortVocal(string src)
    {
        if (src == null || src.Length < 2 || src.Length > 7)
            return false;
        var kana = false;
        foreach (var c in src)
        {
            if (VocalChars.IndexOf(c) < 0)
                return false;
            if (c >= '\u3041' && c <= '\u3096' || c >= '\u30A1' && c <= '\u30FA' || c == '\u30FC')
                kana = true;
        }
        return kana;
    }

    private static bool LookupExact(string src, out string dst)
    {
        if (Exact.TryGetValue(src, out dst) && dst != src)
            return true;
        dst = null;
        return false;
    }

    private static bool TryPrefix(string src, out string dst)
    {
        string bestKey = null;
        foreach (var key in Exact.Keys)
        {
            if (key.Length <= src.Length || !key.StartsWith(src, StringComparison.Ordinal))
                continue;
            if (bestKey == null || key.Length < bestKey.Length)
                bestKey = key;
        }
        if (bestKey == null)
        {
            dst = null;
            return false;
        }
        dst = Exact[bestKey];
        return dst != src;
    }

    private static int IndexOfUnescapedEquals(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '=')
                continue;
            if (i > 0 && line[i - 1] == '\\')
                continue;
            return i;
        }
        return -1;
    }

    private static string Unescape(string s) => s.Replace("\\=", "=").Replace("\\n", "\n");
}
