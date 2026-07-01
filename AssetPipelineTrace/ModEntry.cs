using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.GameData.Objects;

namespace AssetPipelineTrace;

public sealed class ModConfig
{
    public bool EnableChanges { get; set; } = true;
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
    internal static Dictionary<IAssetName, TraceContext> traceCtx = [];
    internal static ModConfig config = null!;

    public override void Entry(IModHelper helper)
    {
        mon = Monitor;
        help = helper;
        config = helper.ReadConfig<ModConfig>();

        harmony.Patch(
            original: AccessTools.DeclaredMethod(typeof(AssetRequestedEventArgs), nameof(AssetRequestedEventArgs.Edit)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(AssetRequestedEventArgs_Edit_Prefix))
        );

        // ap-trace Data/Objects
        // ap-trace Data/TriggerActions
        // ap-trace Data/Objects Data/TriggerActions
        // ap-trace LooseSprites/Cursors
        // ap-trace Maps/Forest
        // ap-trace mushymato.MMAP/Panorama
        help.ConsoleCommands.Add(
            "ap-trace",
            "Trace content edit operations on a list of assets, deactivate all active traces if no arg given",
            ConsoleDoTrace
        );
        help.ConsoleCommands.Add(
            "ap-toggle-changes",
            "Enable/disable data edit changes (very slow)",
            ConsoleToggleChanges
        );
        help.Events.Content.AssetReady += OnAssetReady;

        help.ConsoleCommands.Add("ap-perf", "Test perf on trace", ConsoleTestPerf);
    }

    private void ConsoleTestPerf(string arg1, string[] arg2)
    {
        const int trials = 10;
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
        ActivateTrace(["Data/Objects"]);
        stopwatch.Restart();
        for (int i = 0; i < trials; i++)
        {
            Log(i.ToString());
            help.GameContent.InvalidateCache(assetName);
            var _ = help.GameContent.Load<Dictionary<string, ObjectData>>(assetName);
        }
        Log($"Data/Objects B: {stopwatch.Elapsed}", LogLevel.Info);
        DeactivateTrace();
        help.Events.Content.AssetReady += OnAssetReady;
    }

    private void OnAssetReady(object? sender, AssetReadyEventArgs e)
    {
        foreach (TraceContext ctx in traceCtx.Values)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(ctx.TracedAsset))
            {
                ctx.Deactivate();
            }
        }
    }

    private void ConsoleDoTrace(string cmd, string[] args)
    {
        if (args.Length == 0)
        {
            DeactivateTrace();
        }
        else
        {
            ActivateTrace(args);
        }
    }

    private void ConsoleToggleChanges(string arg1, string[] arg2)
    {
        config.EnableChanges = !config.EnableChanges;
        Log($"EnableChanges: {config.EnableChanges}");
        Helper.WriteConfig(config);
    }

    private static void ActivateTrace(string[] args, [CallerMemberName] string? caller = null)
    {
        bool added = false;
        foreach (string assetName in args)
        {
            IAssetName tracedAsset = help.GameContent.ParseAssetName(assetName);
            if (!traceCtx.TryGetValue(tracedAsset, out TraceContext? ctx))
            {
                ctx = new TraceContext(tracedAsset);
                traceCtx[tracedAsset] = ctx;
            }
            ctx.Activate(caller);
            added = true;
        }
        if (added)
        {
            foreach (IAssetName assetName in traceCtx.Keys.ToList())
            {
                help.GameContent.InvalidateCache(assetName);
            }
        }
    }

    private static void DeactivateTrace([CallerMemberName] string? caller = null)
    {
        if (traceCtx.Count == 0)
        {
            Log("Not active");
            return;
        }
        foreach (TraceContext ctx in traceCtx.Values)
        {
            ctx.Deactivate(caller);
        }
        traceCtx.Clear();
    }

    #region patches

    private static void AssetRequestedEventArgs_Edit_Prefix(
        AssetRequestedEventArgs __instance,
        ref Action<IAssetData> apply,
        AssetEditPriority priority,
        string? onBehalfOf
    )
    {
        foreach (TraceContext ctx in traceCtx.Values)
        {
            ctx.HandleEdit(
                __instance.AssetInfo,
                __instance.Mod,
                __instance.LoadOperations,
                ref apply,
                priority,
                onBehalfOf
            );
        }
    }
    #endregion

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
