using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using xTile;

namespace AssetPipelineTrace;

public class WrappedAssetData(IAssetData original) : IAssetData
{
    public object Data => original.Data;

    public string? Locale => original.Locale;

    public IAssetName Name => original.Name;

    public IAssetName NameWithoutLocale => original.NameWithoutLocale;

    public Type DataType => original.DataType;

    public IAssetDataForDictionary<TKey, TValue> AsDictionary<TKey, TValue>() => original.AsDictionary<TKey, TValue>();

    public IAssetDataForImage AsImage() => new WrappedAssetDataForImage(this, original.AsImage());

    public IAssetDataForMap AsMap() => new WrappedAssetDataForMap(this, original.AsMap());

    public TData GetData<TData>() => original.GetData<TData>();

    public void ReplaceWith(object value)
    {
        Operations.Add($"{nameof(ReplaceWith)}()");
        original.ReplaceWith(value);
    }

    public List<string> Operations = [];
}

public sealed class WrappedAssetDataForImage(WrappedAssetData parent, IAssetDataForImage original) : IAssetDataForImage
{
    public Texture2D Data => original.Data;

    public string? Locale => original.Locale;

    public IAssetName Name => original.Name;

    public IAssetName NameWithoutLocale => original.NameWithoutLocale;

    public Type DataType => original.DataType;

    public bool ExtendImage(int minWidth, int minHeight)
    {
        parent.Operations.Add($"{nameof(ExtendImage)}(minWidth={minWidth},minHeight={minHeight})");
        return original.ExtendImage(minWidth, minHeight);
    }

    public void PatchImage(
        IRawTextureData source,
        Rectangle? sourceArea = null,
        Rectangle? targetArea = null,
        PatchMode patchMode = PatchMode.Replace
    )
    {
        parent.Operations.Add(
            $"{nameof(PatchImage)}(sourceArea={sourceArea},targetArea={targetArea},patchMode={patchMode})"
        );
        original.PatchImage(source, sourceArea, targetArea, patchMode);
    }

    public void PatchImage(
        Texture2D source,
        Rectangle? sourceArea = null,
        Rectangle? targetArea = null,
        PatchMode patchMode = PatchMode.Replace
    )
    {
        parent.Operations.Add(
            $"{nameof(PatchImage)}(sourceArea={sourceArea},targetArea={targetArea},patchMode={patchMode})"
        );
        original.PatchImage(source, sourceArea, targetArea, patchMode);
    }

    public void ReplaceWith(Texture2D value)
    {
        parent.Operations.Add($"{nameof(ReplaceWith)}()");
        original.ReplaceWith(value);
    }
}

public sealed class WrappedAssetDataForMap(WrappedAssetData parent, IAssetDataForMap original) : IAssetDataForMap
{
    public Map Data => original.Data;

    public string? Locale => original.Locale;

    public IAssetName Name => original.Name;

    public IAssetName NameWithoutLocale => original.NameWithoutLocale;

    public Type DataType => original.DataType;

    public bool ExtendMap(int minWidth = 0, int minHeight = 0)
    {
        parent.Operations.Add($"{nameof(ExtendMap)}(minWidth={minWidth},minHeight={minHeight})");
        return original.ExtendMap(minWidth, minHeight);
    }

    public void PatchMap(
        Map source,
        Rectangle? sourceArea = null,
        Rectangle? targetArea = null,
        PatchMapMode patchMode = PatchMapMode.Overlay
    )
    {
        parent.Operations.Add(
            $"{nameof(PatchMap)}(sourceArea={sourceArea},targetArea={targetArea},patchMode={patchMode})"
        );
        original.PatchMap(source, sourceArea, targetArea, patchMode);
    }

    public void ReplaceWith(Map value)
    {
        parent.Operations.Add($"{nameof(ReplaceWith)}()");
        original.ReplaceWith(value);
    }
}
