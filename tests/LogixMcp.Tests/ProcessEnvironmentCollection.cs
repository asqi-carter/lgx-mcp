namespace LogixMcp.Tests;

/// <summary>Collection for tests that mutate process environment variables; xunit runs it apart from parallel collections.</summary>
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public class ProcessEnvironmentCollection;
