using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HarmonyLib;
using Newtonsoft.Json;
using Sickhead.Engine.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewValley;
using StardewValley.GameData.Objects;

namespace AssetPipelineTrace;

public enum TraceKind
{
    Unknown,
    Data,
    Map,
    Image,
}

public enum TraceStep
{
    Load,
    Edit,
}

public interface ITraceFrame
{
    public TraceKind Kind { get; }
    public TraceStep Step { get; }
    public string? ForMod { get; set; }
}

public sealed class DataLoadTraceFrame : ITraceFrame
{
    public TraceKind Kind => TraceKind.Data;
    public TraceStep Step => TraceStep.Load;
    public string? ForMod { get; set; }

    public List<string>? Init { get; set; }
}

public sealed class DataEditTraceFrame : ITraceFrame
{
    public TraceKind Kind => TraceKind.Data;
    public TraceStep Step => TraceStep.Edit;
    public string? ForMod { get; set; }

    public List<string>? Add { get; set; }
    public List<string>? Remove { get; set; }
    public List<string>? Changed { get; set; }
    public AssetEditPriority Priority { get; set; }
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
    internal static Dictionary<string, byte[]>? tracedHashes = null;
    internal static readonly List<ITraceFrame> tracedFrames = [];
    internal static MD5 md5 = MD5.Create();

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
        // ap-trace mushymato.MMAP/Panorama
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
        tracedHashes = null;
        tracedFrames.Clear();
        tracedAsset = null;
    }

    #region patches
    private static bool ShouldTrace(IAssetInfo asset)
    {
        return tracedAsset?.IsEquivalentTo(asset.Name) ?? false;
    }

    private static TraceKind GetTraceKind(IAssetInfo asset)
    {
        if (asset.DataType.IsGenericType)
        {
            Type genericDef = asset.DataType.GetGenericTypeDefinition();
            Type[] genericArgs = asset.DataType.GetGenericArguments();
            if (genericDef == typeof(List<>) || genericDef == typeof(Dictionary<,>) && genericArgs[0] == typeof(string))
            {
                return TraceKind.Data;
            }
        }
        return TraceKind.Unknown;
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
        if (!ShouldTrace(___AssetInfo))
            return;
        switch (GetTraceKind(___AssetInfo))
        {
            case TraceKind.Data:
                HandleEdit_TraceKindData(___AssetInfo, ___Mod, ref apply, priority, onBehalfOf);
                return;
        }
    }
    #endregion

    #region edit handlers
    private static void HandleEdit_TraceKindData(
        IAssetInfo asset,
        IModMetadata mod,
        ref Action<IAssetData> apply,
        AssetEditPriority priority,
        string? onBehalfOf
    )
    {
        MethodInfo? hashGetter = MakeHashDictGetter(asset.DataType);
        if (hashGetter == null)
            return;
        Action<IAssetData> originalApply = apply;
        apply = asset =>
        {
            if (tracedHashes == null)
            {
                tracedHashes = (Dictionary<string, byte[]>)hashGetter.Invoke(null, [asset])!;
                tracedFrames.Add(new DataLoadTraceFrame() { ForMod = null, Init = tracedHashes.Keys.ToList() });
            }
            // original
            originalApply(asset);
            // original

            List<string> added = [];
            List<string> removed = [];
            List<string> changed = [];

            Dictionary<string, byte[]> tracedHashesAfter =
                (Dictionary<string, byte[]>)hashGetter.Invoke(null, [asset])!;
            foreach ((string key, byte[] hash) in tracedHashesAfter)
            {
                if (tracedHashes.TryGetValue(key, out byte[]? hashPrev))
                {
                    if (hashPrev.SequenceCompareTo(hash) != 0)
                    {
                        changed.Add(key);
                    }
                }
                else
                {
                    added.Add(key);
                }
            }
            foreach ((string key, byte[] hash) in tracedHashes)
            {
                if (!tracedHashesAfter.ContainsKey(key))
                    removed.Add(key);
            }
            tracedHashes = tracedHashesAfter;

            tracedFrames.Add(
                new DataEditTraceFrame()
                {
                    ForMod = GetForMod(mod, onBehalfOf),
                    Add = added,
                    Remove = removed,
                    Changed = changed,
                    Priority = priority,
                }
            );
        };
    }
    #endregion

    private static string GetForMod(IModMetadata mod, string? onBehalfOf)
    {
        return onBehalfOf != null ? $"{onBehalfOf} (via {mod.Manifest.UniqueID})" : mod.Manifest.UniqueID;
    }

    private static MethodInfo? MakeHashDictGetter(Type typ)
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

    private static Dictionary<string, byte[]> CheckStringDict<TValue>(IAssetData asset)
    {
        IDictionary<string, TValue> data = asset.AsDictionary<string, TValue>().Data;
        return data.ToDictionary(kv => kv.Key, kv => HashMD5(kv.Value));
    }

    private static Dictionary<string, byte[]> CheckIdList<TValue>(IAssetData asset)
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
        Dictionary<string, byte[]> result = [];
        foreach (TValue item in data)
        {
            result[getId(item)] = HashMD5(item);
        }
        return result;
    }

    /// <summary>Get a MD5 hash by the value for unique key purposes</summary>
    /// <param name="input"></param>
    /// <returns></returns>
    internal static byte[] HashMD5(object? input)
    {
        // Use input string to calculate MD5 hash
        byte[] inputBytes = Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(input));
        return md5.ComputeHash(inputBytes);
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
