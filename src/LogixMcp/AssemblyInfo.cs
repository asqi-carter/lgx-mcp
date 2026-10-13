using System.Runtime.CompilerServices;

// Allow LogixMcp.Tests to access internal members — required for
// SdkAdapterLoaderTests, which exercises GetProbeCandidates,
// DirectoryHasRockwellDlls, and SdkAssemblyLoadContext directly.
[assembly: InternalsVisibleTo("LogixMcp.Tests")]

// SdkImportDatatypeRunner re-uses ExtractTargetMembersFromFragment to parse
// the SDK re-export of an imported datatype.
[assembly: InternalsVisibleTo("LogixMcp.SdkAdapter")]
