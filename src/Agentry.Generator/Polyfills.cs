// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// Makes <c>init</c> accessors and <c>record</c> compile on netstandard2.0.
/// </summary>
/// <remarks>
/// The compiler emits a reference to this type for every <c>init</c> setter, and
/// netstandard2.0 does not have it. Every source generator repository carries
/// this file, because generators must target netstandard2.0 (see the csproj) and
/// value-equatable records are exactly what an incremental pipeline needs.
/// <para>
/// It is <c>internal</c>: two assemblies each declaring it publicly would
/// collide.
/// </para>
/// </remarks>
internal static class IsExternalInit;
