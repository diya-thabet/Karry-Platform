using Karry.BuildingBlocks.Modules;

namespace Karry.Host;

/// <summary>
/// The modules this host runs. Add a module here after generating it with <c>tools/new-module.sh</c>.
/// </summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All { get; } = [];
}
