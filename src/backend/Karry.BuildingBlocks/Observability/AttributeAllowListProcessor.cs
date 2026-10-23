using System.Diagnostics;
using OpenTelemetry;

namespace Karry.BuildingBlocks.Observability;

/// <summary>
/// Removes every span attribute that is not in <see cref="TelemetryAttributes.Allowed"/> before export, so new
/// instrumentation (or a library upgrade) can never start leaking personal data into telemetry by default.
/// Must be registered before any exporter.
/// </summary>
public sealed class AttributeAllowListProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        List<string>? rejected = null;
        foreach (var tag in data.TagObjects)
        {
            if (!TelemetryAttributes.Allowed.Contains(tag.Key))
            {
                (rejected ??= []).Add(tag.Key);
            }
        }

        if (rejected is null)
        {
            return;
        }

        foreach (var key in rejected)
        {
            data.SetTag(key, null); // null removes the tag
        }
    }
}
