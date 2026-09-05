#if NETSTANDARD2_1

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// Required by the compiler to emit <c>init</c> accessors and records when targeting
/// netstandard2.1, which predates the type in the BCL.
/// </summary>
internal static class IsExternalInit
{
}

#endif
