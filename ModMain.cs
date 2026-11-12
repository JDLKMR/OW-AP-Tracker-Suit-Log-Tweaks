using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using UnityEngine;

namespace APTrackerSuitLogTweaks;

/// <summary>
/// Small companion mod for Ixrec.ArchipelagoRandomizer and dgarro.SuitLog. It does three things:
///
///   1. (AP Randomizer) When you back out of a category in the AP Tracker, put the cursor back on that
///      category instead of the top of the list.
///   2. (AP Randomizer + Suit Log) In the Suit Log only, remember the cursor position in the AP Tracker's
///      category list after leaving the tracker entirely (closing the Suit Log or switching modes), and
///      restore it when you come back. The Ship Log always starts from the top.
///   3. (Suit Log) Opening the Suit Log with the d-pad "up" button no longer also counts as pressing "up"
///      in the list (which used to throw the cursor to the bottom of the list).
///
/// Everything is done by reflection at runtime, so there is no compile-time dependency on either mod,
/// and each tweak simply does nothing if the mod it needs isn't installed.
/// </summary>
public class ModMain : ModBehaviour
{
    internal static ModMain Instance;

    private const string ApModUniqueName = "Ixrec.ArchipelagoRandomizer";
    private const string SuitLogModUniqueName = "dgarro.SuitLog";
    private const string ChecklistModeTypeName = "ArchipelagoRandomizer.InGameTracker.APChecklistMode";

    private void Start()
    {
        Instance = this;

        bool hasSuitLog = ModHelper.Interaction.ModExists(SuitLogModUniqueName);
        bool hasAp = ModHelper.Interaction.ModExists(ApModUniqueName);

        SuitLogBridge.ModPresent = hasSuitLog;

        // The two patch sets are independent: Suit Log tweak 3 is useful without the AP mod, and the
        // AP tweaks still work (minus the Suit Log-only part) without Suit Log.
        if (hasSuitLog)
            StartCoroutine(PatchSuitLogWhenLoaded());
        if (hasAp)
            StartCoroutine(PatchApWhenLoaded());
    }

    private void Update()
    {
        SuitLogPatches.Tick();
    }

    private IEnumerator PatchApWhenLoaded()
    {
        // The AP mod's DLL may not be loaded yet when our Start() runs, so retry for a few seconds.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            Type type = AccessTools.TypeByName(ChecklistModeTypeName);
            if (type != null)
            {
                ApTrackerPatches.Install(type);
                yield break;
            }
            yield return new WaitForSeconds(0.5f);
        }
        Log("Couldn't find the AP Randomizer tracker code. Nothing was patched.", MessageType.Warning);
    }

    private IEnumerator PatchSuitLogWhenLoaded()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (SuitLogBridge.TryResolve())
            {
                SuitLogPatches.Install();
                yield break;
            }
            yield return new WaitForSeconds(0.5f);
        }
        Log("Couldn't find the Suit Log code. Nothing was patched for it.", MessageType.Warning);
    }

    internal static void Log(string message, MessageType type = MessageType.Info)
    {
        Instance?.ModHelper.Console.WriteLine("[AP Tracker/Suit Log Tweaks] " + message, type);
    }
}

internal static class Shared
{
    internal static readonly Harmony Harmony = new Harmony("JD.APTrackerSuitLogTweaks");
}

/// <summary>
/// Read-only reflection access to Suit Log's internals.
/// </summary>
internal static class SuitLogBridge
{
    private const string SuitLogTypeName = "SuitLog.SuitLog";
    private const string ListNavigatorTypeName = "SuitLog.ListNavigator";

    // Set by ModMain.Start, so we never go hunting through assemblies when Suit Log isn't installed.
    internal static bool ModPresent;

    internal static Type SuitLogType;
    internal static Type ListNavigatorType;

    private static FieldInfo instanceField;
    private static FieldInfo openField;
    private static bool resolved;
    private static bool failed;

    /// <summary>Finds Suit Log's types. Returns false if they aren't loaded yet (or never will be).</summary>
    internal static bool TryResolve()
    {
        if (resolved) return true;
        if (failed || !ModPresent) return false;

        SuitLogType = AccessTools.TypeByName(SuitLogTypeName);
        ListNavigatorType = AccessTools.TypeByName(ListNavigatorTypeName);
        if (SuitLogType == null || ListNavigatorType == null)
            return false; // Probably just not loaded yet; the caller retries

        instanceField = AccessTools.Field(SuitLogType, "Instance");
        openField = AccessTools.Field(SuitLogType, "_open");
        if (instanceField == null || openField == null)
        {
            // The types exist, so retrying won't help: Suit Log's code must have changed.
            failed = true;
            ModMain.Log("Suit Log's code has changed, couldn't find its Instance/_open fields. " +
                        "Suit Log-specific behavior is disabled.", MessageType.Warning);
            return false;
        }

        resolved = true;
        return true;
    }

    /// <summary>
    /// True while the Suit Log is open. Suit Log sets _open before it enters the current mode and
    /// only clears it after the mode has exited, so this is reliable from inside a mode's
    /// EnterMode/ExitMode. In the Ship Log it's always false.
    /// </summary>
    internal static bool IsOpen()
    {
        try
        {
            if (!TryResolve()) return false;
            object suitLog = instanceField.GetValue(null);
            return suitLog != null && (bool)openField.GetValue(suitLog);
        }
        catch (Exception e)
        {
            ModMain.Log("Couldn't read the Suit Log's open state: " + e, MessageType.Error);
            return false;
        }
    }
}

/// <summary>
/// Tweak 3: don't let the button that opens the Suit Log also move the list cursor.
/// </summary>
internal static class SuitLogPatches
{
    private static bool installed;

    // Set when the Suit Log opens; cleared once the list-up button has been let go of.
    private static bool waitForUpRelease;

    internal static void Install()
    {
        if (installed) return;

        try
        {
            MethodBase open = AccessTools.Method(SuitLogBridge.SuitLogType, "OpenSuitLog");
            MethodBase getSelectionChange = AccessTools.Method(SuitLogBridge.ListNavigatorType, "GetSelectionChange");

            string missing = "";
            if (open == null) missing += " OpenSuitLog";
            if (getSelectionChange == null) missing += " GetSelectionChange";
            if (missing != "")
            {
                ModMain.Log("Suit Log's code has changed, couldn't find:" + missing + ". Nothing was patched for it.", MessageType.Warning);
                return;
            }

            Shared.Harmony.Patch(open,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SuitLogPatches), nameof(OpenSuitLogPrefix))));
            Shared.Harmony.Patch(getSelectionChange,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SuitLogPatches), nameof(GetSelectionChangePrefix))));

            installed = true;
            ModMain.Log("Patched the Suit Log.", MessageType.Success);
        }
        catch (Exception e)
        {
            ModMain.Log("Failed to patch the Suit Log: " + e, MessageType.Error);
        }
    }

    private static bool IsListUpHeld()
    {
        // Same inputs as Suit Log's own Input.Action.ListUp
        return OWInput.IsPressed(InputLibrary.up) || OWInput.IsPressed(InputLibrary.up2);
    }

    /// <summary>Called every frame by ModMain.Update.</summary>
    internal static void Tick()
    {
        if (!waitForUpRelease) return;
        try
        {
            if (!IsListUpHeld())
                waitForUpRelease = false;
        }
        catch (Exception)
        {
            waitForUpRelease = false; // Never leave the list stuck
        }
    }

    private static void OpenSuitLogPrefix()
    {
        waitForUpRelease = true;
    }

    // Returning false skips the original method and uses __result (0 = "don't move").
    // Skipping it entirely also means its hold-to-repeat timers stay at zero until the button is released.
    private static bool GetSelectionChangePrefix(ref int __result)
    {
        if (!waitForUpRelease) return true;

        try
        {
            if (IsListUpHeld())
            {
                __result = 0;
                return false;
            }
        }
        catch (Exception e)
        {
            ModMain.Log("GetSelectionChange prefix failed: " + e, MessageType.Error);
        }

        waitForUpRelease = false;
        return true;
    }
}

/// <summary>
/// Tweaks 1 and 2: the AP Tracker's category list.
/// </summary>
internal static class ApTrackerPatches
{
    private static bool installed;

    private static FieldInfo inChecklistField;
    private static FieldInfo selectionWrapperField;
    private static FieldInfo displayedCategoriesField;
    private static MethodInfo selectItemMethod;
    private static MethodInfo getIndexMethod;
    private static MethodInfo setIndexMethod;
    private static MethodInfo updateListUIMethod;

    // The category the cursor was on when the AP Tracker was last left while the Suit Log was open.
    // Deliberately only ever read/written for the Suit Log, so the Ship Log always starts at the top.
    private static int lastSuitLogIndex = -1;

    // True only while the tracker is running EnterMode inside the Suit Log, so that the saved position is
    // restored when the tracker is entered and not on some other call to OpenSelectionMode.
    private static bool enteringInSuitLog;
    private static bool enterModePatched;

    internal static void Install(Type modeType)
    {
        if (installed) return;

        try
        {
            MethodBase openSelectionMode = AccessTools.Method(modeType, "OpenSelectionMode");
            inChecklistField = AccessTools.Field(modeType, "IsInChecklist");
            selectionWrapperField = AccessTools.Field(modeType, "SelectionWrapper");
            displayedCategoriesField = AccessTools.Field(modeType, "displayedCategories");
            selectItemMethod = AccessTools.Method(modeType, "SelectSelectionItem", new[] { typeof(int) });

            if (selectionWrapperField != null)
            {
                Type wrapperType = selectionWrapperField.FieldType;
                getIndexMethod = AccessTools.Method(wrapperType, "GetSelectedIndex");
                setIndexMethod = AccessTools.Method(wrapperType, "SetSelectedIndex", new[] { typeof(int) });
                updateListUIMethod = AccessTools.Method(wrapperType, "UpdateListUI");
            }

            string missing = "";
            if (openSelectionMode == null) missing += " OpenSelectionMode";
            if (inChecklistField == null) missing += " IsInChecklist";
            if (selectionWrapperField == null) missing += " SelectionWrapper";
            if (displayedCategoriesField == null) missing += " displayedCategories";
            if (selectItemMethod == null) missing += " SelectSelectionItem";
            if (getIndexMethod == null) missing += " GetSelectedIndex";
            if (setIndexMethod == null) missing += " SetSelectedIndex";
            if (updateListUIMethod == null) missing += " UpdateListUI";
            if (missing != "")
            {
                ModMain.Log("The AP mod's code has changed, couldn't find:" + missing + ". Nothing was patched.", MessageType.Warning);
                return;
            }

            Shared.Harmony.Patch(
                openSelectionMode,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ApTrackerPatches), nameof(OpenPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ApTrackerPatches), nameof(OpenPostfix))));

            // These two are only needed for remembering the position across Suit Log sessions.
            // If either is missing the rest still works.
            MethodBase enterMode = AccessTools.Method(modeType, "EnterMode");
            MethodBase exitMode = AccessTools.Method(modeType, "ExitMode");
            if (enterMode != null && exitMode != null)
            {
                Shared.Harmony.Patch(
                    enterMode,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ApTrackerPatches), nameof(EnterPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(ApTrackerPatches), nameof(EnterPostfix))));
                Shared.Harmony.Patch(
                    exitMode,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ApTrackerPatches), nameof(ExitPrefix))));
                enterModePatched = true;
            }
            else
            {
                ModMain.Log("Couldn't find the AP Tracker's EnterMode/ExitMode, so its position won't be remembered between Suit Log sessions.", MessageType.Warning);
            }

            installed = true;
            ModMain.Log("Patched the AP Tracker.", MessageType.Success);
        }
        catch (Exception e)
        {
            ModMain.Log("Failed to patch the AP Tracker: " + e, MessageType.Error);
        }
    }

    private static int GetWrapperIndex(object instance)
    {
        object wrapper = selectionWrapperField.GetValue(instance);
        return wrapper == null ? -1 : (int)getIndexMethod.Invoke(wrapper, null);
    }

    // Decides which category (if any) the cursor should be put back on after the AP mod resets it to the top.
    private static void OpenPrefix(object __instance, out int __state)
    {
        __state = -1;
        try
        {
            if ((bool)inChecklistField.GetValue(__instance))
            {
                // Backing out of a category (in either log): the AP mod sets IsInChecklist to false
                // before opening the tracker fresh, so this is the one case where we're coming back from an entry.
                __state = GetWrapperIndex(__instance);
            }
            else if (enterModePatched && enteringInSuitLog)
            {
                // Entering the tracker fresh, in the Suit Log: go back to where we left off.
                // (In the Ship Log we leave __state alone, so it starts from the top as usual.)
                __state = lastSuitLogIndex;
            }
        }
        catch (Exception e)
        {
            ModMain.Log("Prefix failed: " + e, MessageType.Error);
            __state = -1;
        }
    }

    private static void OpenPostfix(object __instance, int __state)
    {
        if (__state < 0) return;
        try
        {
            ICollection categories = displayedCategoriesField.GetValue(__instance) as ICollection;
            if (categories == null || __state >= categories.Count) return;

            object wrapper = selectionWrapperField.GetValue(__instance);
            if (wrapper == null) return;

            setIndexMethod.Invoke(wrapper, new object[] { __state });
            updateListUIMethod.Invoke(wrapper, null);
            selectItemMethod.Invoke(__instance, new object[] { __state });
        }
        catch (Exception e)
        {
            ModMain.Log("Postfix failed: " + e, MessageType.Error);
        }
    }

    private static void EnterPrefix()
    {
        enteringInSuitLog = SuitLogBridge.IsOpen();
    }

    private static void EnterPostfix()
    {
        enteringInSuitLog = false;
    }

    // The AP Tracker is being left (Suit Log closed, or switched to another Suit Log mode).
    // The wrapper's index is still the category index here even if we were inside a category, since that's
    // exactly what the back-out-of-a-category restore above relies on.
    private static void ExitPrefix(object __instance)
    {
        enteringInSuitLog = false; // In case EnterMode threw and skipped its postfix
        try
        {
            if (!SuitLogBridge.IsOpen()) return; // Ship Log: never remembered

            int index = GetWrapperIndex(__instance);
            ICollection categories = displayedCategoriesField.GetValue(__instance) as ICollection;
            if (index >= 0 && categories != null && index < categories.Count)
                lastSuitLogIndex = index;
        }
        catch (Exception e)
        {
            ModMain.Log("ExitMode prefix failed: " + e, MessageType.Error);
        }
    }
}
