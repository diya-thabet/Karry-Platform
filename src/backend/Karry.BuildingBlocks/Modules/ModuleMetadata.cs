namespace Karry.BuildingBlocks.Modules;

/// <summary>Endpoint metadata: the module that owns the endpoint (telemetry attribute <c>module</c>, RED metrics).</summary>
public sealed record ModuleMetadata(string Name);
