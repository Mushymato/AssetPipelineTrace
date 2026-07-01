using System.Reflection;
using System.Runtime.CompilerServices;
using Force.DeepCloner;
using JsonDiffPatchDotNet;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json.Linq;
using Sickhead.Engine.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Content;
using xTile;
using xTile.ObjectModel;

namespace AssetPipelineTrace;

public sealed class TraceContext(IAssetName tracedAsset)
{
    internal bool Active { get; private set; } = false;
    internal Dictionary<string, JToken?>? tracedJToken = null;
    internal readonly List<ITraceFrame> tracedFrames = [];
    internal IAssetName TracedAsset => tracedAsset;

    public void Activate([CallerMemberName] string? caller = null)
    {
        if (Active)
            return;
        tracedJToken = null;
        tracedFrames.Clear();
        ModEntry.Log($"ACTIVATE({caller}) {tracedAsset}", LogLevel.Info);
        Active = true;
    }

    public void Deactivate([CallerMemberName] string? caller = null)
    {
        if (!Active)
            return;
        string filename = string.Concat(
            "trace-",
            string.Join('_', tracedAsset!.Name.Split(Path.GetInvalidFileNameChars())),
            ".json"
        );
        ModEntry.help.Data.WriteJsonFile(filename, tracedFrames);
        ModEntry.Log(
            $"DEACTIVATE({caller}) {tracedAsset}, wrote {tracedFrames.Count} frames to '{Path.Combine(ModEntry.help.DirectoryPath, filename)}'",
            LogLevel.Info
        );
        Active = false;
    }

    private bool ShouldTrace(IAssetInfo asset)
    {
        return Active && (tracedAsset?.IsEquivalentTo(asset.Name) ?? false);
    }

    private static TraceKind GetTraceKind(IAssetInfo asset)
    {
        if (asset.DataType == typeof(Map))
        {
            return TraceKind.Map;
        }
        if (asset.DataType == typeof(Texture2D))
        {
            return TraceKind.Image;
        }
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

    public void HandleEdit(
        IAssetInfo asset,
        IModMetadata? mod,
        List<AssetLoadOperation> loadOperations,
        ref Action<IAssetData> apply,
        AssetEditPriority priority = AssetEditPriority.Default,
        string? onBehalfOf = null
    )
    {
        if (!ShouldTrace(asset))
            return;
        TraceKind kind = GetTraceKind(asset);
        switch (kind)
        {
            case TraceKind.Data:
                HandleEdit_TraceKindData(asset, mod, loadOperations, ref apply, priority, onBehalfOf);
                break;
            case TraceKind.Map:
            case TraceKind.Image:
                HandleEdit_TraceKindOps(kind, mod, loadOperations, ref apply, priority, onBehalfOf);
                break;
        }
    }

    internal static JsonDiffPatch jdp = new();

    private void HandleEdit_TraceKindData(
        IAssetInfo asset,
        IModMetadata? mod,
        List<AssetLoadOperation> loadOperations,
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
            if (tracedJToken == null)
            {
                tracedJToken = (Dictionary<string, JToken?>)hashGetter.Invoke(null, [asset])!;
                AssetLoadOperation? loader = loadOperations.MaxBy(p => p.Priority);
                tracedFrames.Add(
                    new DataLoadTraceFrame()
                    {
                        ForMod =
                            loader != null
                                ? GetForMod(loader.Mod, loader.OnBehalfOf?.Manifest.UniqueID)
                                : "StardewValley",
                        Init = tracedJToken.Keys.ToList(),
                    }
                );
            }
            // original
            originalApply(asset);
            // original

            List<string> added = [];
            List<string> removed = [];
            Dictionary<string, JToken> changed = [];

            Dictionary<string, JToken?> tracedJTokenAfter =
                (Dictionary<string, JToken?>)hashGetter.Invoke(null, [asset])!;
            foreach ((string key, JToken? tok) in tracedJTokenAfter)
            {
                if (tracedJToken.TryGetValue(key, out JToken? tokPrev))
                {
                    if (jdp.Diff(tokPrev, tok) is JToken diff)
                        changed[key] = diff;
                }
                else
                {
                    added.Add(key);
                }
            }
            foreach ((string key, JToken? hash) in tracedJToken)
            {
                if (!tracedJTokenAfter.ContainsKey(key))
                    removed.Add(key);
            }
            tracedJToken = tracedJTokenAfter;

            tracedFrames.Add(
                new DataEditTraceFrame()
                {
                    ForMod = GetForMod(mod, onBehalfOf),
                    Add = added,
                    Remove = removed,
                    Changed = ModEntry.config.EnableDetailedChanges ? changed : null,
                    Priority = priority,
                }
            );
        };
    }

    private void HandleEdit_TraceKindOps(
        TraceKind kind,
        IModMetadata? mod,
        List<AssetLoadOperation> loadOperations,
        ref Action<IAssetData> apply,
        AssetEditPriority priority,
        string? onBehalfOf
    )
    {
        Action<IAssetData> originalApply = apply;
        apply = asset =>
        {
            if (tracedFrames.Count == 0)
            {
                AssetLoadOperation? loader = loadOperations.MaxBy(p => p.Priority);
                tracedFrames.Add(
                    new AreaLoadTraceFrame(kind)
                    {
                        ForMod =
                            loader != null
                                ? GetForMod(loader.Mod, loader.OnBehalfOf?.Manifest.UniqueID)
                                : "StardewValley",
                    }
                );
            }

            IPropertyCollection? oldProps = null;
            if (ModEntry.config.EnableDetailedChanges && kind == TraceKind.Map)
            {
                ModEntry.ChangedMapTiles = [];
                oldProps = asset.AsMap().Data.Properties.ShallowClone();
            }
            WrappedAssetData wrappedAsset = new(asset);
            // original
            originalApply(wrappedAsset);
            // original

            List<string> operations = wrappedAsset.Operations;
            Dictionary<Point, string>? changedTilesDesc = null;
            if (kind == TraceKind.Map)
            {
                if (ModEntry.ChangedMapTiles?.Any() ?? false)
                {
                    changedTilesDesc = [];
                    foreach (MapTileChange change in ModEntry.ChangedMapTiles)
                    {
                        operations.Add($"TileChanged({change})");
                        changedTilesDesc[new(change.X, change.Y)] = change.ToString();
                    }
                }
                if (oldProps != null)
                {
                    List<string>? mapProps = ModEntry.GetChangedProps(oldProps, wrappedAsset.AsMap().Data.Properties);
                    if (mapProps != null)
                    {
                        foreach (string prop in mapProps)
                            operations.Add($"PropChanged({prop})");
                    }
                }
            }
            ModEntry.ChangedMapTiles = null;
            tracedFrames.Add(
                new AreaEditTraceFrame(kind)
                {
                    ForMod = GetForMod(mod, onBehalfOf),
                    Operations = operations,
                    Priority = priority,
                    ChangedTilesDesc = changedTilesDesc,
                }
            );
        };
    }

    private static string GetForMod(IModMetadata? mod, string? onBehalfOf)
    {
        string modId = mod?.Manifest.UniqueID ?? "UNKNOWN";
        return onBehalfOf != null ? $"{onBehalfOf} (via {modId})" : modId;
    }

    private static MethodInfo? MakeHashDictGetter(Type typ)
    {
        Type genericDef = typ.GetGenericTypeDefinition();
        Type[] genericArgs = typ.GetGenericArguments();
        if (genericDef == typeof(Dictionary<,>) && genericArgs[0] == typeof(string))
        {
            return CheckStringDictInfo?.MakeGenericMethod(genericArgs[1]);
        }
        else if (genericDef == typeof(List<>))
        {
            return CheckIdListInfo?.MakeGenericMethod(genericArgs[0]);
        }
        return null;
    }

    private static readonly MethodInfo? CheckStringDictInfo = typeof(TraceContext).GetMethod(
        nameof(CheckStringDict),
        BindingFlags.Static | BindingFlags.NonPublic
    );

    private static Dictionary<string, JToken?> CheckStringDict<TValue>(IAssetData asset)
    {
        IDictionary<string, TValue> data = asset.AsDictionary<string, TValue>().Data;
        return data.ToDictionary(kv => kv.Key, kv => MakeDiff(kv.Value));
    }

    private static readonly MethodInfo? CheckIdListInfo = typeof(TraceContext).GetMethod(
        nameof(CheckIdList),
        BindingFlags.Static | BindingFlags.NonPublic
    );

    private static Dictionary<string, JToken?> CheckIdList<TValue>(IAssetData asset)
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
        Dictionary<string, JToken?> result = [];
        foreach (TValue item in data)
        {
            result[getId(item)] = MakeDiff(item);
        }
        return result;
    }

    private static JToken? MakeDiff(object? item) =>
        ModEntry.config.EnableDetailedChanges ? JToken.FromObject(item!) : null;
}
