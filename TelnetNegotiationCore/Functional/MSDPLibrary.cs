using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Functional;

/// <summary>
/// Translation between an MSDP byte stream and JSON, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// MSDP (https://tintin.mudhalla.net/protocols/msdp/) encodes a tree of variables as a flat byte
/// sequence: <c>MSDP_VAR</c> introduces a key, <c>MSDP_VAL</c> its value, and tables and arrays are
/// bracketed by <c>MSDP_TABLE_OPEN</c>/<c>MSDP_TABLE_CLOSE</c> and
/// <c>MSDP_ARRAY_OPEN</c>/<c>MSDP_ARRAY_CLOSE</c>. Everything else is text.
/// </para>
/// <para>
/// <see cref="MSDPScan"/> reads that encoding into nested dictionaries, lists and strings, which the
/// caller hands to <see cref="JsonSerializer"/>; <see cref="Report"/> is the inverse, taking a JSON
/// document and writing the byte sequence a peer expects.
/// </para>
/// </remarks>
public static class MSDPLibrary
{
    /// <summary>
    /// The deepest nesting <see cref="MSDPScan"/> will follow before rejecting the message.
    /// </summary>
    /// <remarks>
    /// A subnegotiation payload comes from an untrusted peer and its nesting is what decides the
    /// recursion depth here, so without a bound a hostile peer could nest a megabyte of
    /// <c>MSDP_VAR</c>/<c>MSDP_VAL</c> pairs and overflow the stack — which is not catchable. Real
    /// MSDP data nests two or three levels (a table of tables); 64 leaves that untouched.
    /// </remarks>
    public const int MaxDepth = 64;

    private const byte MsdpVar = (byte)Trigger.MSDP_VAR;
    private const byte MsdpVal = (byte)Trigger.MSDP_VAL;
    private const byte MsdpTableOpen = (byte)Trigger.MSDP_TABLE_OPEN;
    private const byte MsdpTableClose = (byte)Trigger.MSDP_TABLE_CLOSE;
    private const byte MsdpArrayOpen = (byte)Trigger.MSDP_ARRAY_OPEN;
    private const byte MsdpArrayClose = (byte)Trigger.MSDP_ARRAY_CLOSE;

    /// <summary>
    /// Reads an MSDP byte sequence into an object graph.
    /// </summary>
    /// <param name="array">The MSDP payload, without the surrounding <c>IAC SB MSDP</c> framing.</param>
    /// <param name="encoding">The encoding the peer is sending text in.</param>
    /// <returns>
    /// A <see cref="SortedDictionary{TKey,TValue}"/> for a table, a <see cref="List{T}"/> for an
    /// array, or a <see cref="string"/> for a bare value — nested as the payload was. Keys are
    /// ordinal-sorted, so the same variables always serialize to the same JSON regardless of the
    /// order the peer sent them in.
    /// </returns>
    /// <exception cref="InvalidDataException">The payload nests deeper than <see cref="MaxDepth"/>.</exception>
    public static object MSDPScan(IEnumerable<byte> array, Encoding encoding)
    {
        if (array is null) throw new ArgumentNullException(nameof(array));
        if (encoding is null) throw new ArgumentNullException(nameof(encoding));

        var buffer = array as byte[] ?? array.ToArray();
        var (value, _) = Scan(buffer, 0, NewTable(), encoding, 0);
        return value;
    }

    /// <summary>
    /// Writes a JSON object as the payload of an MSDP subnegotiation: a sequence of
    /// <c>MSDP_VAR &lt;name&gt; MSDP_VAL &lt;value&gt;</c> pairs.
    /// </summary>
    /// <param name="jsonObject">The variables to report, as a JSON object.</param>
    /// <param name="encoding">The encoding to write text in.</param>
    /// <returns>The payload, to be framed as <c>IAC SB MSDP … IAC SE</c>.</returns>
    /// <remarks>
    /// This is the top-level form every example in the specification uses —
    /// <c>IAC SB MSDP MSDP_VAR "HINT" MSDP_VAL "THE GAME" IAC SE</c> — and it is not the same as
    /// <see cref="Report"/>, which encodes a JSON <em>value</em> and so wraps an object in
    /// <c>MSDP_TABLE_OPEN</c>/<c>MSDP_TABLE_CLOSE</c>. A table belongs around a nested value, not
    /// around the payload itself.
    /// </remarks>
    /// <exception cref="JsonException"><paramref name="jsonObject"/> is not valid JSON.</exception>
    /// <exception cref="InvalidDataException"><paramref name="jsonObject"/> is not a JSON object.</exception>
    public static byte[] ReportVariables(string jsonObject, Encoding encoding)
    {
        if (encoding is null) throw new ArgumentNullException(nameof(encoding));

        if (JsonNode.Parse(jsonObject) is not JsonObject variables)
        {
            throw new InvalidDataException(
                "An MSDP payload is a sequence of variables, so it must be written from a JSON object.");
        }

        var output = new List<byte>();

        foreach (var variable in variables)
        {
            output.Add(MsdpVar);
            WriteText(variable.Key, encoding, output);
            output.Add(MsdpVal);
            WriteValue(variable.Value, encoding, output);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Writes a JSON document as the MSDP byte sequence carrying the same data.
    /// </summary>
    /// <param name="jsonString">The JSON document to report.</param>
    /// <param name="encoding">The encoding to write text in.</param>
    /// <returns>The MSDP payload, without the surrounding <c>IAC SB MSDP</c> framing.</returns>
    /// <remarks>
    /// MSDP has no types beyond text, so booleans are reported as <c>1</c>/<c>0</c> and null as
    /// <c>-1</c>, the conventional spellings.
    /// </remarks>
    /// <exception cref="JsonException"><paramref name="jsonString"/> is not valid JSON.</exception>
    public static byte[] Report(string jsonString, Encoding encoding)
    {
        if (encoding is null) throw new ArgumentNullException(nameof(encoding));

        var output = new List<byte>();
        WriteValue(JsonNode.Parse(jsonString), encoding, output);
        return output.ToArray();
    }

    /// <summary>
    /// Reads one value starting at <paramref name="index"/>, accumulating into
    /// <paramref name="accumulator"/>, and returns it with the index just past what it consumed.
    /// </summary>
    private static (object Value, int Next) Scan(
        byte[] buffer, int index, object accumulator, Encoding encoding, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException(
                $"MSDP message nests deeper than {MaxDepth} levels and was rejected.");
        }

        while (index < buffer.Length)
        {
            switch (buffer[index])
            {
                // A variable: its name runs to the MSDP_VAL that introduces its value.
                case MsdpVar:
                {
                    var keyStart = index + 1;
                    var keyEnd = keyStart;
                    while (keyEnd < buffer.Length && buffer[keyEnd] != MsdpVal) keyEnd++;

                    var key = encoding.GetString(buffer, keyStart, keyEnd - keyStart);
                    var (value, next) = Scan(buffer, keyEnd, NewTable(), encoding, depth + 1);

                    // One variable may carry several values without an array around them - the
                    // specification's own SEND example is MSDP_VAR "SEND" MSDP_VAL "AREA_NAME"
                    // MSDP_VAL "ROOM_NAME". They belong to the variable just read, so collect them
                    // rather than reading the second one as the start of something new.
                    if (next < buffer.Length && buffer[next] == MsdpVal)
                    {
                        var values = new List<object> { value };

                        while (next < buffer.Length && buffer[next] == MsdpVal)
                        {
                            var (extra, after) = Scan(buffer, next, NewTable(), encoding, depth + 1);
                            values.Add(extra);
                            next = after;
                        }

                        value = values;
                    }

                    ((SortedDictionary<string, object>)accumulator)[key] = value;
                    index = next;
                    continue;
                }

                case MsdpVal:
                    // Inside an array every MSDP_VAL introduces another element; anywhere else it
                    // introduces the one value being read, so start it fresh.
                    if (accumulator is List<object> array)
                    {
                        var (value, next) = Scan(buffer, index + 1, NewTable(), encoding, depth + 1);
                        array.Add(value);
                        index = next;
                        continue;
                    }

                    if (accumulator is SortedDictionary<string, object>) accumulator = NewTable();
                    index++;
                    continue;

                case MsdpTableOpen:
                    accumulator = NewTable();
                    index++;
                    continue;

                case MsdpArrayOpen:
                    accumulator = new List<object>();
                    index++;
                    continue;

                case MsdpTableClose:
                case MsdpArrayClose:
                    return (accumulator, index + 1);

                // Text, running to the next control byte.
                default:
                {
                    var end = index;
                    while (end < buffer.Length && buffer[end] > MsdpArrayClose) end++;
                    return (encoding.GetString(buffer, index, end - index), end);
                }
            }
        }

        return (accumulator, index);
    }

    private static void WriteValue(JsonNode? node, Encoding encoding, List<byte> output)
    {
        // A null property value parses to a null node rather than to one of kind Null.
        if (node is null)
        {
            WriteText("-1", encoding, output);
            return;
        }

        switch (node.GetValueKind())
        {
            case JsonValueKind.Object:
                output.Add(MsdpTableOpen);
                foreach (var property in node.AsObject())
                {
                    output.Add(MsdpVar);
                    WriteText(property.Key, encoding, output);
                    output.Add(MsdpVal);
                    WriteValue(property.Value, encoding, output);
                }

                output.Add(MsdpTableClose);
                break;

            case JsonValueKind.Array:
                output.Add(MsdpArrayOpen);
                foreach (var item in node.AsArray())
                {
                    output.Add(MsdpVal);
                    WriteValue(item, encoding, output);
                }

                output.Add(MsdpArrayClose);
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
                WriteText(node.AsValue().ToString(), encoding, output);
                break;

            case JsonValueKind.True:
                WriteText("1", encoding, output);
                break;

            case JsonValueKind.False:
                WriteText("0", encoding, output);
                break;

            case JsonValueKind.Null:
                WriteText("-1", encoding, output);
                break;

            default:
                throw new InvalidDataException($"Invalid JSON value: {node.ToJsonString()}");
        }
    }

    private static void WriteText(string text, Encoding encoding, List<byte> output) =>
        output.AddRange(encoding.GetBytes(text));

    /// <summary>
    /// Ordinal-sorted so that the JSON a caller serializes from a scan is stable.
    /// </summary>
    private static SortedDictionary<string, object> NewTable() => new(StringComparer.Ordinal);
}
