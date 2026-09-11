#if !NET11_0_OR_GREATER
// The two types the C# 15 compiler needs to treat a type as a union. .NET 11 ships them in
// System.Runtime; below that the language expects them to be defined locally, and every target is
// compiled with the .NET 11 SDK, so ByteOrTrigger is a union on netstandard2.0, net8.0 and net10.0 too.
namespace System.Runtime.CompilerServices
{
	/// <summary>Marks a class or struct as a union type.</summary>
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
	internal sealed class UnionAttribute : Attribute;

	/// <summary>Runtime access to the value a union holds.</summary>
	internal interface IUnion
	{
		/// <summary>The value of the union, or null.</summary>
		object? Value { get; }
	}
}
#endif
