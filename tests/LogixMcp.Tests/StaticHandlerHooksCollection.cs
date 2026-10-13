namespace LogixMcp.Tests;

/// <summary>
/// Collection for tests that swap static handler hooks (for example
/// <c>CloseProjectHandler.SdkSessionInspector</c>); SDK connect registration sets the same hooks,
/// so xunit runs this collection apart from the parallel ones.
/// </summary>
[CollectionDefinition("StaticHandlerHooks", DisableParallelization = true)]
public class StaticHandlerHooksCollection;
