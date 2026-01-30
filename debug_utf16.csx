using System;
using System.Text;
using System.Linq;

var encoding = Encoding.Unicode; // UTF-16 LE
var testString = "UTF-16 Test \u00FF Special: \u4E2D\u6587";

Console.WriteLine($"Original string: {testString}");
Console.WriteLine($"String length: {testString.Length}");

// Convert to bytes
var bytes = encoding.GetBytes(testString);
Console.WriteLine($"\nOriginal bytes ({bytes.Length} total):");
Console.WriteLine(string.Join(" ", bytes.Select(b => $"{b:X2}")));

// Check for IAC bytes (255)
var iacCount = bytes.Count(b => b == 255);
Console.WriteLine($"\nIAC bytes (255) count: {iacCount}");
Console.WriteLine($"Positions of 255: {string.Join(", ", bytes.Select((b, i) => (b, i)).Where(x => x.b == 255).Select(x => x.i))}");

// Decode back
var decoded = encoding.GetString(bytes);
Console.WriteLine($"\nDecoded string: {decoded}");
Console.WriteLine($"Match: {decoded == testString}");

// Try decoding just first few bytes to see what "ｕ" might be
Console.WriteLine("\nTrying to decode subsets:");
for (int i = 2; i <= Math.Min(10, bytes.Length); i += 2)
{
    var subset = bytes.Take(i).ToArray();
    var subDecode = encoding.GetString(subset);
    Console.WriteLine($"  First {i} bytes: \"{subDecode}\" = {string.Join(" ", subset.Select(b => $"{b:X2}"))}");
}

// What is "ｕ"?
var weirdChar = "ｕ";
var weirdBytes = encoding.GetBytes(weirdChar);
Console.WriteLine($"\n\"ｕ\" as bytes: {string.Join(" ", weirdBytes.Select(b => $"{b:X2}"))}");
