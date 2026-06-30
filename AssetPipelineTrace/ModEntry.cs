using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using Sickhead.Engine.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.GameData.Objects;
using xTile;

namespace AssetPipelineTrace;

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
    internal static List<TraceContext> traceCtx = [];

    internal static MethodInfo originalEdit = AccessTools.DeclaredMethod(
        typeof(AssetRequestedEventArgs),
        nameof(AssetRequestedEventArgs.Edit)
    );

    public override void Entry(IModHelper helper)
    {
        mon = Monitor;
        help = helper;

        harmony.Patch(
            original: originalEdit,
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(AssetRequestedEventArgs_Edit_Prefix))
        );

        // ap-trace Data/Objects
        // ap-trace Data/TriggerActions
        // ap-trace Data/Objects Data/TriggerActions
        // ap-trace LooseSprites/Cursors
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
        List<TraceContext> doneCtx = [];
        foreach (TraceContext ctx in traceCtx)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(ctx.TracedAsset))
            {
                ctx.Deactivate();
                doneCtx.Add(ctx);
            }
        }
        traceCtx.RemoveAll(doneCtx.Contains);
    }

    private void ConsoleDoTrace(string cmd, string[] args)
    {
        if (args.Length < 1)
        {
            Log("Need at least 1 argument", LogLevel.Error);
            return;
        }
        if (traceCtx.Count == 0)
        {
            ActivateTrace(args);
        }
        else
        {
            DeactivateTrace();
        }
    }

    private static void ActivateTrace(string[] args, [CallerMemberName] string? caller = null)
    {
        bool wasActive = traceCtx.Count > 0;
        foreach (string assetName in args)
        {
            IAssetName tracedAsset = help.GameContent.ParseAssetName(assetName);
            Log($"ACTIVATE({caller}) {assetName}", LogLevel.Info);
            traceCtx.Add(new TraceContext(tracedAsset));
        }
        if (!wasActive && traceCtx.Count > 0)
        {
            foreach (IAssetName assetName in traceCtx.Select(ctx => ctx.TracedAsset).ToList())
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
        foreach (TraceContext ctx in traceCtx)
        {
            ctx.Deactivate(caller);
        }
        traceCtx.Clear();
    }

    #region patches

    private static void AssetRequestedEventArgs_Edit_Prefix(
        IAssetInfo ___AssetInfo,
        IModMetadata ___Mod,
        ref Action<IAssetData> apply,
        AssetEditPriority priority = AssetEditPriority.Default,
        string? onBehalfOf = null
    )
    {
        if (traceCtx.Count == 0)
            return;
        foreach (TraceContext ctx in traceCtx)
        {
            ctx.Handle(___AssetInfo, ___Mod, ref apply, priority, onBehalfOf);
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
