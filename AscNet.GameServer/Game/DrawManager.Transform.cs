namespace AscNet.GameServer.Game;

internal static partial class DrawManager
{
    // XUiGridDrawGroupBanner opens UiAwarenessTfChoice from the active draw group's TransformSuitList,
    // so a suit is refittable exactly while some active group advertises it.
    internal static bool IsTransformSuitAvailable(int suitId) =>
        GroupTemplates.Any(group => IsActive(group) && group.TransformSuitList.Contains(suitId));
}
