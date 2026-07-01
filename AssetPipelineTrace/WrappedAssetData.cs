using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using xTile;

namespace AssetPipelineTrace;

public sealed class WrappedAssetData(IAssetData original) : IAssetData
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
        AddOperation();
        original.ReplaceWith(value);
    }

    public List<string> Operations = [];
    public List<Rectangle> Areas = [];

    public void AddOperation(int minWidth, int minHeight, [CallerMemberName] string? caller = null)
    {
        Operations.Add($"{caller}(minWidth={minWidth},minHeight={minHeight})");
    }

    public void AddOperation(
        Rectangle? sourceArea,
        Rectangle? targetArea,
        string patchMode,
        [CallerMemberName] string? caller = null
    )
    {
        Operations.Add($"{caller}(sourceArea={sourceArea},targetArea={targetArea},patchMode={patchMode})");
        if (targetArea.HasValue)
            Areas.Add(targetArea.Value);
    }

    public void AddOperation([CallerMemberName] string? caller = null)
    {
        Operations.Add($"{caller}()");
    }
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
        parent.AddOperation(minWidth, minHeight);
        return original.ExtendImage(minWidth, minHeight);
    }

    public void PatchImage(
        IRawTextureData source,
        Rectangle? sourceArea = null,
        Rectangle? targetArea = null,
        PatchMode patchMode = PatchMode.Replace
    )
    {
        parent.AddOperation(sourceArea, targetArea, patchMode.ToString());
        original.PatchImage(source, sourceArea, targetArea, patchMode);
    }

    public void PatchImage(
        Texture2D source,
        Rectangle? sourceArea = null,
        Rectangle? targetArea = null,
        PatchMode patchMode = PatchMode.Replace
    )
    {
        parent.AddOperation(sourceArea, targetArea, patchMode.ToString());
        original.PatchImage(source, sourceArea, targetArea, patchMode);
    }

    public void ReplaceWith(Texture2D value)
    {
        parent.AddOperation();
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
        parent.AddOperation(minWidth, minHeight);
        return original.ExtendMap(minWidth, minHeight);
    }

    public void PatchMap(
        Map source,
        Rectangle? sourceArea = null,
        Rectangle? targetArea = null,
        PatchMapMode patchMode = PatchMapMode.Overlay
    )
    {
        parent.AddOperation(sourceArea, targetArea, patchMode.ToString());
        original.PatchMap(source, sourceArea, targetArea, patchMode);
    }

    public void ReplaceWith(Map value)
    {
        parent.AddOperation();
        original.ReplaceWith(value);
    }
}
