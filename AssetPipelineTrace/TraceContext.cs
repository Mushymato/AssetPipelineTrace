using System.Reflection;
using System.Runtime.CompilerServices;
using JsonDiffPatchDotNet;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json.Linq;
using Sickhead.Engine.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using xTile;

namespace AssetPipelineTrace;

public sealed class TraceContext(IAssetName tracedAsset)
{
    internal Dictionary<string, JToken>? tracedJToken = null;
    internal readonly List<ITraceFrame> tracedFrames = [];
    internal IAssetName TracedAsset => tracedAsset;

    public string Deactivate([CallerMemberName] string? caller = null)
    {
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
        return Path.Combine(ModEntry.help.DirectoryPath, filename);
    }

    private bool ShouldTrace(IAssetInfo asset)
    {
        return tracedAsset?.IsEquivalentTo(asset.Name) ?? false;
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

    public void Handle(
        IAssetInfo asset,
        IModMetadata mod,
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
                HandleEdit_TraceKindData(asset, mod, ref apply, priority, onBehalfOf);
                break;
            case TraceKind.Map:
            case TraceKind.Image:
                HandleEdit_TraceKindOps(kind, mod, ref apply, priority, onBehalfOf);
                break;
        }
    }

    internal static JsonDiffPatch jdp = new();

    private void HandleEdit_TraceKindData(
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
            if (tracedJToken == null)
            {
                tracedJToken = (Dictionary<string, JToken>)hashGetter.Invoke(null, [asset])!;
                tracedFrames.Add(new DataLoadTraceFrame() { ForMod = null, Init = tracedJToken.Keys.ToList() });
            }
            // original
            originalApply(asset);
            // original

            List<string> added = [];
            List<string> removed = [];
            Dictionary<string, JToken> changed = [];

            Dictionary<string, JToken> tracedHashesAfter =
                (Dictionary<string, JToken>)hashGetter.Invoke(null, [asset])!;
            foreach ((string key, JToken tok) in tracedHashesAfter)
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
            foreach ((string key, JToken hash) in tracedJToken)
            {
                if (!tracedHashesAfter.ContainsKey(key))
                    removed.Add(key);
            }
            tracedJToken = tracedHashesAfter;

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

    private void HandleEdit_TraceKindOps(
        TraceKind kind,
        IModMetadata mod,
        ref Action<IAssetData> apply,
        AssetEditPriority priority,
        string? onBehalfOf
    )
    {
        Action<IAssetData> originalApply = apply;
        apply = asset =>
        {
            WrappedAssetData wrappedAsset = new(asset);
            // original
            originalApply(wrappedAsset);
            // original
            tracedFrames.Add(
                new OpsEditTraceFrame(kind)
                {
                    ForMod = GetForMod(mod, onBehalfOf),
                    Operations = wrappedAsset.Operations,
                    Priority = priority,
                }
            );
        };
    }

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

    private static Dictionary<string, JToken> CheckStringDict<TValue>(IAssetData asset)
    {
        IDictionary<string, TValue> data = asset.AsDictionary<string, TValue>().Data;
        return data.ToDictionary(kv => kv.Key, kv => JToken.FromObject(kv.Value!));
    }

    private static readonly MethodInfo? CheckIdListInfo = typeof(TraceContext).GetMethod(
        nameof(CheckIdList),
        BindingFlags.Static | BindingFlags.NonPublic
    );

    private static Dictionary<string, JToken> CheckIdList<TValue>(IAssetData asset)
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
        Dictionary<string, JToken> result = [];
        foreach (TValue item in data)
        {
            result[getId(item)] = JToken.FromObject(item!);
        }
        return result;
    }
}
