using System.Runtime.CompilerServices;

// Allow LogixMcp.Tests to access internal members — required for
// SdkConnectFactoryTests, which instantiates SdkConnectFactory via its
// internal testing constructor.
[assembly: InternalsVisibleTo("LogixMcp.Tests")]
