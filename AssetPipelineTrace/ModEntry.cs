using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;
using xTile;
using xTile.Dimensions;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;

namespace AssetPipelineTrace;

public sealed class ModConfig
{
    public bool EnableDetailedChanges { get; set; } = true;
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
    internal static IAssetName? TracedMap = null;
    internal static TraceContext? TracedMapReady = null;

    public override void Entry(IModHelper helper)
    {
        mon = Monitor;
        help = helper;
        config = helper.ReadConfig<ModConfig>();

        harmony.Patch(
            original: AccessTools.DeclaredMethod(typeof(AssetRequestedEventArgs), nameof(AssetRequestedEventArgs.Edit)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(AssetRequestedEventArgs_Edit_Prefix))
        );
        foreach (PropertyInfo prop in AccessTools.GetDeclaredProperties(typeof(TileArray)))
        {
            if (prop.PropertyType != typeof(Tile))
                continue;
            ParameterInfo[] paramInfo = prop.GetIndexParameters();
            if (
                paramInfo.Length == 2
                && paramInfo[0].ParameterType == typeof(int)
                && paramInfo[1].ParameterType == typeof(int)
            )
            {
                Log($"Patched Setter of {prop}");
                harmony.Patch(
                    original: prop.GetSetMethod(),
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(TileArray_Item_XY_Prefix)),
                    postfix: new HarmonyMethod(typeof(ModEntry), nameof(TileArray_Item_XY_Postfix))
                );
            }
            else if (paramInfo.Length == 1 && paramInfo[0].ParameterType == typeof(Location))
            {
                Log($"Patched Setter of {prop}");
                harmony.Patch(
                    original: prop.GetSetMethod(),
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(TileArray_Item_Location_Prefix)),
                    postfix: new HarmonyMethod(typeof(ModEntry), nameof(TileArray_Item_Location_Postfix))
                );
            }
        }

        // ap-trace Data/Objects
        // ap-trace Data/TriggerActions
        // ap-trace Data/Objects Data/TriggerActions
        // ap-trace LooseSprites/Cursors
        // ap-trace Maps/Forest
        // ap-trace Maps/Backwoods
        // ap-trace mushymato.MMAP/Panorama
        help.ConsoleCommands.Add(
            "ap-trace",
            "Trace content edit operations on a list of assets, deactivate all active traces if no arg given",
            ConsoleDoTrace
        );
        help.ConsoleCommands.Add(
            "ap-trace-map",
            "Trace the current location's map and start logging mods that edits each clicked tile. Run this again to stop.",
            ConsoleDoTraceMap
        );
        help.ConsoleCommands.Add(
            "ap-toggle-details",
            "Enable/disable detailed edit changes (is slower)",
            ConsoleToggleDetails
        );
        help.Events.Content.AssetReady += OnAssetReady;
        help.Events.Input.CursorMoved += OnCursorMoved;

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
                if (TracedMap?.IsEquivalentTo(ctx.TracedAsset) ?? false)
                {
                    TracedMapReady = ctx;
                }
            }
        }
    }

    /// https://github.com/Pathoschild/StardewMods/blob/76565e83ede4bc8b3c293f1c659032ba9c39c213/Common/TileHelper.cs#L123
    /// <summary>Get the tile at the non-UI pixel coordinate relative to the top-left corner of the screen.</summary>
    /// <param name="x">The pixel X coordinate.</param>
    /// <param name="y">The pixel Y coordinate.</param>
    public static Point GetTileFromScreenPosition(float x, float y)
    {
        float screenX = Game1.viewport.X + x / Game1.options.zoomLevel;
        float screenY = Game1.viewport.Y + y / Game1.options.zoomLevel;

        int tileX = (int)Math.Floor(screenX / Game1.tileSize);
        int tileY = (int)Math.Floor(screenY / Game1.tileSize);

        return new Point(tileX, tileY);
    }

    private Point prevPoint = Point.Zero;

    private void OnCursorMoved(object? sender, CursorMovedEventArgs e)
    {
        if (TracedMapReady != null)
        {
            if (
                Game1.currentLocation == null
                || !TracedMapReady.TracedAsset.IsEquivalentTo(Game1.currentLocation.mapPath.Value)
            )
            {
                Log("Stopped tracing the map", LogLevel.Info);
                TracedMap = null;
                TracedMapReady = null;
                return;
            }
            Point tile = GetTileFromScreenPosition(Game1.getMouseXRaw(), Game1.getMouseYRaw());
            if (prevPoint == tile)
                return;
            prevPoint = tile;
            Log($"===== {tile} =====", LogLevel.Debug);

            foreach (ITraceFrame frame in TracedMapReady.tracedFrames)
            {
                if (
                    frame is not AreaEditTraceFrame areaFrame
                    || areaFrame.Kind != TraceKind.Map
                    || areaFrame.ChangedTilesDesc == null
                )
                    continue;
                if (areaFrame.ChangedTilesDesc.TryGetValue(tile, out string? desc))
                {
                    Log(string.Concat(desc, " - ", areaFrame.ForMod), LogLevel.Info);
                }
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

    private void ConsoleDoTraceMap(string cmd, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Log("Must load a save first", LogLevel.Error);
            return;
        }
        if (TracedMap != null)
        {
            Log("Stopped tracing the map", LogLevel.Info);
            TracedMap = null;
            TracedMapReady = null;
            return;
        }
        if (Game1.currentLocation.Map is not Map map || Game1.currentLocation.mapPath.Value is not string mapPath)
        {
            Log("Current location map is null", LogLevel.Error);
            return;
        }
        DeactivateTrace();
        ActivateTrace([mapPath], true);
    }

    private void ConsoleToggleDetails(string arg1, string[] arg2)
    {
        config.EnableDetailedChanges = !config.EnableDetailedChanges;
        Log($"EnableChanges: {config.EnableDetailedChanges}");
        Helper.WriteConfig(config);
    }

    private static void ActivateTrace(string[] args, bool isTracedMap = false, [CallerMemberName] string? caller = null)
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
            if (isTracedMap)
            {
                TracedMap = tracedAsset;
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

    internal static Map? ChangingMap = null;
    internal static List<MapTileChange>? ChangedMapTiles = null;

    internal static List<string>? GetChangedProps(IPropertyCollection oldProps, IPropertyCollection newProps)
    {
        List<string>? props = null;
        foreach ((string key, PropertyValue value) in oldProps)
        {
            if (newProps.TryGetValue(key, out PropertyValue? newValue))
            {
                if (value != newValue)
                {
                    (props ??= []).Add($"'{key}'='{newValue}'");
                }
            }
            else
            {
                (props ??= []).Add($"'{key}'=null");
            }
            continue;
        }
        foreach ((string key, PropertyValue value) in newProps)
        {
            if (!oldProps.ContainsKey(key))
            {
                (props ??= []).Add($"'{key}'='{value}'");
            }
        }
        return props;
    }

    private static void CheckTileChanged(Tile? oldTile, Tile newTile, string layer, int x, int y)
    {
        if (oldTile == null && newTile == null)
            return;
        if (oldTile == null != (newTile == null))
        {
            ChangedMapTiles!.Add(new(layer, x, y, null));
            return;
        }
        if (oldTile == null || newTile == null)
            return;

        List<string>? props = GetChangedProps(oldTile.Properties, newTile.Properties);
        if ((oldTile.TileIndex != newTile.TileIndex) || (oldTile.TileSheet.Id != newTile.TileSheet.Id) || props != null)
        {
            ChangedMapTiles!.Add(new(layer, x, y, props));
        }
    }

    private static bool NoCheckTile(Layer ___m_layer)
    {
        return ChangingMap == null || ChangedMapTiles == null || ___m_layer.Map != ChangingMap;
    }

    private static void TileArray_Item_XY_Prefix(
        TileArray __instance,
        Layer ___m_layer,
        ref Tile? __state,
        int x,
        int y
    )
    {
        if (NoCheckTile(___m_layer))
            return;
        __state = __instance[x, y];
    }

    private static void TileArray_Item_XY_Postfix(
        TileArray __instance,
        Layer ___m_layer,
        ref Tile? __state,
        int x,
        int y
    )
    {
        if (NoCheckTile(___m_layer))
            return;
        Tile? newTile = __instance[x, y];
        CheckTileChanged(__state, newTile, ___m_layer.Id, x, y);
    }

    private static void TileArray_Item_Location_Prefix(
        TileArray __instance,
        Layer ___m_layer,
        ref Tile? __state,
        Location location
    )
    {
        if (NoCheckTile(___m_layer))
            return;
        __state = __instance[location];
    }

    private static void TileArray_Item_Location_Postfix(
        TileArray __instance,
        Layer ___m_layer,
        ref Tile? __state,
        Location location
    )
    {
        if (NoCheckTile(___m_layer))
            return;
        Tile? newTile = __instance[location];
        CheckTileChanged(__state, newTile, ___m_layer.Id, location.X, location.Y);
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
