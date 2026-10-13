namespace LogixMcp.Tests;

/// <summary>
/// Collection for tests that read or write audit-logger process state that is not per-context
/// (<c>AuditLogger.LastConnect</c>, <c>LastSdkServiceDiagnosis</c>, the global failure counters,
/// <c>LGX_AUDIT_*</c> variables, <c>Console.SetError</c>); xunit runs it apart from parallel collections.
/// </summary>
[CollectionDefinition("AuditLoggerDir", DisableParallelization = true)]
public class AuditLoggerDirCollection;
