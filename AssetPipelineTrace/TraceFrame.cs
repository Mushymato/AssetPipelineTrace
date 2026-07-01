using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI.Events;

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
    public Dictionary<string, JToken>? Changed { get; set; }
    public AssetEditPriority Priority { get; set; }
}

public sealed class AreaEditTraceFrame(TraceKind kind) : ITraceFrame
{
    public TraceKind Kind => kind;
    public TraceStep Step => TraceStep.Edit;
    public string? ForMod { get; set; }
    public List<string>? Operations { get; set; }
    public List<Rectangle> Areas { get; set; }
    public AssetEditPriority Priority { get; set; }
}
