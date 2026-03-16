#if NETSTANDARD2_0
using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore;

/// <summary>
/// Polyfills for APIs available in netstandard2.1+ but missing in netstandard2.0.
/// </summary>
internal static class NetStandard20Polyfills
{
	/// <summary>
	/// Polyfill for <see cref="HashSet{T}.TryGetValue"/> (added in netstandard2.1).
	/// Note: Returns the input value rather than the stored value. This is correct for
	/// default equality comparers but may differ from the real API with custom comparers.
	/// </summary>
	public static bool TryGetValue<T>(this HashSet<T> set, T equalValue, out T actualValue)
	{
		if (set.Contains(equalValue))
		{
			actualValue = equalValue;
			return true;
		}
		actualValue = default!;
		return false;
	}

	/// <summary>
	/// Polyfill for <see cref="Dictionary{TKey,TValue}.TryAdd"/> (added in netstandard2.1).
	/// </summary>
	public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
	{
		if (!dictionary.ContainsKey(key))
		{
			dictionary.Add(key, value);
			return true;
		}
		return false;
	}

	/// <summary>
	/// Polyfill for <see cref="Enumerable.ToHashSet{T}(IEnumerable{T})"/> (added in netstandard2.1).
	/// </summary>
	public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source)
	{
		return new HashSet<T>(source);
	}

	/// <summary>
	/// Polyfill for KeyValuePair deconstruction (added in netstandard2.1).
	/// </summary>
	public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kvp, out TKey key, out TValue value)
	{
		key = kvp.Key;
		value = kvp.Value;
	}
}
#endif
