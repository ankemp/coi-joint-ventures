using System.Reflection;
using Mafi;
using Mafi.Core.Entities;
using Mafi.Core.Input;

namespace COIJointVentures.Integration;

internal static class CoiReflection
{
    public static MethodInfo? FindInputSchedulerProcessCommandsMethod()
    {
        return typeof(InputScheduler).GetMethod("ProcessCommands", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static FieldInfo? s_resolverField;
    private static InputScheduler? s_lastScheduler;
    private static EntitiesManager? s_cachedEntitiesManager;

    /// <summary>
    /// Returns a deterministic hash of simulation state for desync detection.
    /// Registered as <see cref="Session.MultiplayerSession.GameStateProbe"/>.
    /// </summary>
    public static int ComputeSimHash()
    {
        var scheduler = Runtime.PluginRuntime.Scheduler;
        if (scheduler == null) return 0;

        // Re-populate cache whenever the scheduler instance changes (new game/load).
        if (!ReferenceEquals(scheduler, s_lastScheduler))
        {
            s_lastScheduler = scheduler;
            s_cachedEntitiesManager = null;
            TryPopulateEntitiesManagerCache(scheduler);
        }

        if (s_cachedEntitiesManager == null) return 0;

        unchecked
        {
            return 17 * 31 + s_cachedEntitiesManager.EntitiesCount;
        }
    }

    private static void TryPopulateEntitiesManagerCache(InputScheduler scheduler)
    {
        s_resolverField ??= typeof(InputScheduler).GetField(
            "m_resolver",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (s_resolverField?.GetValue(scheduler) is not DependencyResolver resolver)
            return;

        foreach (var obj in resolver.AllResolvedInstances)
        {
            if (obj is EntitiesManager em)
            {
                s_cachedEntitiesManager = em;
                return;
            }
        }
    }
}
