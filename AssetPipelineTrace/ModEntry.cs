using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sickhead.Engine.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewValley;
using StardewValley.GameData.Objects;

namespace AssetPipelineTrace;

public sealed class EditTraceFrame
{
    public string? Log;
    public List<string>? Add;
    public List<string>? Remove;
}

public sealed class ModEntry : Mod
{
#if DEBUG
    private const LogLevel DEFAULT_LOG_LEVEL = LogLevel.Debug;
#else
    private const LogLevel DEFAULT_LOG_LEVEL = LogLevel.Trace;
#endif

    public const string ModId = "mushymato.AssetPipelineTrace";
    private static IMonitor mon = null!;
    internal static IModHelper help = null!;
    internal static Harmony harmony = new(ModId);
    internal static IAssetName? tracedAsset = null;
    internal static HashSet<string> tracedKeys = [];
    internal static readonly List<EditTraceFrame> tracedFrames = [];
    internal static MethodInfo originalEdit = AccessTools.DeclaredMethod(
        typeof(AssetRequestedEventArgs),
        nameof(AssetRequestedEventArgs.Edit)
    );

    public override void Entry(IModHelper helper)
    {
        mon = Monitor;
        help = helper;

        // ap-trace Data/Objects
        // ap-trace Data/TriggerActions
        help.ConsoleCommands.Add("ap-trace", "Trace content edit operations on a particular asset", ConsoleDoTrace);
        help.Events.Content.AssetReady += OnAssetReady;

        help.ConsoleCommands.Add("ap-perf", "Test perf on trace", ConsoleTestPerf);
    }

    private void ConsoleTestPerf(string arg1, string[] arg2)
    {
        const int trials = 100;
        const string assetName = "Data/Objects";
        // baseline
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < trials; i++)
        {
            help.GameContent.InvalidateCache(assetName);
            var _ = help.GameContent.Load<Dictionary<string, ObjectData>>(assetName);
        }
        Log($"Data/Objects A: {stopwatch.Elapsed}", LogLevel.Info);
        help.Events.Content.AssetReady -= OnAssetReady;
        ActivateTrace("Data/Objects");
        stopwatch.Restart();
        for (int i = 0; i < trials; i++)
        {
            help.GameContent.InvalidateCache(assetName);
            var _ = help.GameContent.Load<Dictionary<string, ObjectData>>(assetName);
        }
        Log($"Data/Objects B: {stopwatch.Elapsed}", LogLevel.Info);
        DeactivateTrace();
        help.Events.Content.AssetReady += OnAssetReady;
    }

    private void OnAssetReady(object? sender, AssetReadyEventArgs e)
    {
        if (tracedAsset?.IsEquivalentTo(e.NameWithoutLocale) ?? false)
        {
            DeactivateTrace();
        }
    }

    private void ConsoleDoTrace(string cmd, string[] args)
    {
        if (!ArgUtility.TryGet(args, 0, out string? value, out string error))
        {
            Log(error, LogLevel.Error);
            return;
        }
        if (tracedAsset == null)
        {
            ActivateTrace(value);
        }
        else
        {
            DeactivateTrace();
        }
    }

    private static void ActivateTrace(string assetName, [CallerMemberName] string? caller = null)
    {
        tracedAsset = help.GameContent.ParseAssetName(assetName);
        Log($"ACTIVATE({caller}) {assetName}", LogLevel.Info);
        harmony.Patch(
            original: originalEdit,
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(AssetRequestedEventArgs_Edit_Prefix))
        );
        help.GameContent.InvalidateCache(tracedAsset);
    }

    private static void DeactivateTrace([CallerMemberName] string? caller = null)
    {
        if (tracedAsset == null)
        {
            Log("Not active");
        }
        harmony.Unpatch(originalEdit, HarmonyPatchType.Prefix);
        string filename = string.Concat(
            "trace-",
            string.Join('_', tracedAsset!.Name.Split(Path.GetInvalidFileNameChars())),
            ".json"
        );
        help.Data.WriteJsonFile(filename, tracedFrames);
        Log(
            $"DEACTIVATE({caller}) {tracedAsset}, wrote {tracedFrames.Count} frames to '{Path.Combine(help.DirectoryPath, filename)}'",
            LogLevel.Info
        );
        tracedKeys.Clear();
        tracedFrames.Clear();
        tracedAsset = null;
    }

    private static void AssetRequestedEventArgs_Edit_Prefix(
        IAssetInfo ___AssetInfo,
        IModMetadata ___Mod,
        ref Action<IAssetData> apply,
        AssetEditPriority priority = AssetEditPriority.Default,
        string? onBehalfOf = null
    )
    {
        if (tracedAsset == null)
            return;
        if (!ShouldTraceThis(___AssetInfo))
            return;
        MethodInfo? keyGetter = MakeKeyGetter(___AssetInfo.DataType);
        if (keyGetter == null)
            return;
        Action<IAssetData> originalApply = apply;
        apply = asset =>
        {
            if (tracedKeys.Count == 0)
            {
                tracedKeys = (HashSet<string>)keyGetter.Invoke(null, [asset])!;
            }
            // original
            originalApply(asset);
            // original
            HashSet<string> tracedKeysAfter = (HashSet<string>)keyGetter.Invoke(null, [asset])!;
            List<string> added = tracedKeysAfter.Except(tracedKeys).ToList();
            // List<string> removed = tracedKeys.Except(tracedKeysAfter).ToList();
            // string logStr =
            //     $"({___Mod.Manifest.UniqueID} @ {priority}) editing: {asset.Name}, added {added.Count} removed {removed.Count}";
            // if (onBehalfOf != null)
            //     logStr = string.Concat(logStr, $" (for {onBehalfOf})");
            tracedKeys = tracedKeysAfter;
            // Log(logStr, LogLevel.Info);
            tracedFrames.Add(
                new()
                {
                    // Log = string.Empty,
                    Add = added,
                    // Remove = removed,
                }
            );
        };
    }

    private static bool ShouldTraceThis(IAssetInfo asset)
    {
        if (!(tracedAsset?.IsEquivalentTo(asset.Name) ?? false))
            return false;
        if (!asset.DataType.IsGenericType)
            return false;
        return true;
    }

    private static MethodInfo? MakeKeyGetter(Type typ)
    {
        Type genericDef = typ.GetGenericTypeDefinition();
        Type[] genericArgs = typ.GetGenericArguments();
        if (genericDef == typeof(Dictionary<,>) && genericArgs[0] == typeof(string))
        {
            return typeof(ModEntry)
                .GetMethod(nameof(CheckStringDict), BindingFlags.Static | BindingFlags.NonPublic)
                ?.MakeGenericMethod(genericArgs[1]);
        }
        else if (genericDef == typeof(List<>))
        {
            return typeof(ModEntry)
                .GetMethod(nameof(CheckIdList), BindingFlags.Static | BindingFlags.NonPublic)
                ?.MakeGenericMethod(genericArgs[0]);
        }
        Log($"Type not supported, aborting trace", LogLevel.Error);
        DeactivateTrace();
        return null;
    }

    private static HashSet<string> CheckStringDict<TValue>(IAssetData asset)
    {
        IDictionary<string, TValue> data = asset.AsDictionary<string, TValue>().Data;
        return data.Keys.ToHashSet();
    }

    private static HashSet<string> CheckIdList<TValue>(IAssetData asset)
    {
        Func<TValue, string>? getId = null;
        if (
            (typeof(TValue).GetProperty("Id") ?? typeof(TValue).GetProperty("ID")) is PropertyInfo propInfo
            && propInfo.GetDataType() == typeof(string)
        )
        {
            getId = propInfo.GetGetMethod()?.CreateDelegate<Func<TValue, string>>();
        }
        else if (
            (typeof(TValue).GetField("Id") ?? typeof(TValue).GetField("ID")) is FieldInfo fieldInfo
            && fieldInfo.GetDataType() == typeof(string)
        )
        {
            getId = (thing) => (string)fieldInfo.GetValue(thing)!;
        }
        if (getId == null)
            throw new Exception("Failed to get Id/ID prop/field");

        IList<TValue> data = asset.GetData<IList<TValue>>();
        HashSet<string> result = [];
        foreach (TValue item in data)
        {
            result.Add(getId(item));
        }
        return result;
    }

    /// <summary>SMAPI static monitor Log wrapper</summary>
    /// <param name="msg"></param>
    /// <param name="level"></param>
    internal static void Log(string msg, LogLevel level = DEFAULT_LOG_LEVEL)
    {
        mon.Log(msg, level);
    }

    /// <summary>SMAPI static monitor LogOnce wrapper</summary>
    /// <param name="msg"></param>
    /// <param name="level"></param>
    internal static void LogOnce(string msg, LogLevel level = DEFAULT_LOG_LEVEL)
    {
        mon.LogOnce(msg, level);
    }

    /// <summary>SMAPI static monitor Log wrapper, debug only</summary>
    /// <param name="msg"></param>
    /// <param name="level"></param>
    [Conditional("DEBUG")]
    internal static void LogDebug(string msg, LogLevel level = DEFAULT_LOG_LEVEL)
    {
        mon.Log(msg, level);
    }
}
