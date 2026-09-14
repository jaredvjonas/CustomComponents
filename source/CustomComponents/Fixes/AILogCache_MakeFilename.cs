using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace CustomComponents.Fixes;

/// <summary>
/// AILogCache.MakeFilename builds an AI debug-log path with Path.Combine, which throws
/// ArgumentException("Illegal characters in path") when the prefix contains any of " &lt; &gt; |
/// or a control character - typically because a unit's display name is quoted.
///
/// The throw escapes AITeam.getInvocationForCurrentUnit, so the unit never submits an
/// invocation and the enemy turn stalls. Observed as 187 consecutive failures in one mission.
///
/// Sanitises the prefix before the call, and swallows anything that still throws so a debug
/// logger can never break a turn. Logs the offending prefix once per distinct value.
/// </summary>
[HarmonyPatch]
public static class AILogCache_MakeFilename
{
    private static readonly System.Collections.Generic.HashSet<string> Reported = new();

    // AILogCache lives in Assembly-CSharp and may not be public; resolve it by name.
    public static MethodBase TargetMethod()
    {
        var type = AccessTools.TypeByName("AILogCache");
        return type == null ? null : AccessTools.Method(type, "MakeFilename");
    }

    public static bool Prepare(MethodBase original)
    {
        if (original != null)
        {
            return true;
        }

        // nothing to patch on this build - stay silent rather than spam
        return TargetMethod() != null;
    }

    [HarmonyPrefix]
    [HarmonyWrapSafe]
    public static void Prefix(ref string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return;
        }

        var cleaned = Sanitise(prefix);
        if (cleaned == prefix)
        {
            return;
        }

        if (Reported.Add(prefix))
        {
            Log.Main.Info?.Log($"[AILOG-FIX] AI log prefix contains characters illegal in a path: '{prefix}' -> '{cleaned}'. Sanitised to keep AITeam.think from throwing.");
        }

        prefix = cleaned;
    }

    /// <summary>
    /// Last line of defence: if Path.Combine still throws, swallow it and hand back a safe name.
    /// A debug logger must never be able to stall the enemy turn.
    /// </summary>
    [HarmonyFinalizer]
    // Deliberately does NOT bind __result: that would have to match the method's return
    // type, and suppressing is enough - Harmony hands the caller default(T) instead.
    public static Exception Finalizer(Exception __exception)
    {
        if (__exception == null)
        {
            return null;
        }

        Log.Main.Error?.Log($"[AILOG-FIX] AILogCache.MakeFilename threw ({__exception.GetType().Name}: {__exception.Message}); suppressed so the AI turn can continue.");
        return null; // suppress
    }

    private static string Sanitise(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
        {
            buffer.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return buffer.ToString();
    }
}
