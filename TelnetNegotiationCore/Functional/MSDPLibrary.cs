using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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
/// <see cref="MSDPScan"/> reads that encoding into nested dictionaries, lists and strings, and
/// <see cref="ScanToJson"/> reads it straight into JSON; <see cref="Report"/> and
/// <see cref="ReportVariables(string, Encoding)"/> are the inverse, writing the byte sequence a peer
/// expects.
/// </para>
/// <para>
/// Nothing here reflects over a type, so the whole path survives trimming and Native AOT. To carry
/// your own type across, hand in the <see cref="JsonTypeInfo{T}"/> that
/// <see cref="System.Text.Json.Serialization.JsonSerializable"/> source generation produces for it —
/// <see cref="Scan{T}"/> reads a message into one, and <see cref="ReportVariables{T}(T, Encoding, JsonTypeInfo{T})"/>
/// writes one out.
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
    /// <exception cref="InvalidDataException">
    /// The payload nests deeper than <see cref="MaxDepth"/>, or declares a variable inside an array.
    /// </exception>
    public static object MSDPScan(IEnumerable<byte> array, Encoding encoding)
    {
        if (array is null) throw new ArgumentNullException(nameof(array));
        if (encoding is null) throw new ArgumentNullException(nameof(encoding));

        var buffer = array as byte[] ?? array.ToArray();
        var (value, _) = Scan(buffer, 0, NewTable(), encoding, 0);
        return value;
    }

    /// <summary>
    /// Reads an MSDP byte sequence into JSON.
    /// </summary>
    /// <param name="array">The MSDP payload, without the surrounding <c>IAC SB MSDP</c> framing.</param>
    /// <param name="encoding">The encoding the peer is sending text in.</param>
    /// <returns>The message as a JSON document: a table becomes an object, an array an array.</returns>
    /// <remarks>
    /// The JSON is written directly from what was scanned rather than through
    /// <see cref="JsonSerializer"/>, so no type is reflected over and the path is safe to trim.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// The payload nests deeper than <see cref="MaxDepth"/>, or declares a variable inside an array.
    /// </exception>
    public static string ScanToJson(IEnumerable<byte> array, Encoding encoding)
    {
        using var json = ScanToUtf8Json(array, encoding);

        return json.TryGetBuffer(out var buffer) && buffer.Array is not null
            ? Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count)
            : Encoding.UTF8.GetString(json.ToArray());
    }

    /// <summary>
    /// Reads an MSDP byte sequence into a type of your own.
    /// </summary>
    /// <typeparam name="T">The type the message describes.</typeparam>
    /// <param name="array">The MSDP payload, without the surrounding <c>IAC SB MSDP</c> framing.</param>
    /// <param name="encoding">The encoding the peer is sending text in.</param>
    /// <param name="typeInfo">
    /// The contract for <typeparamref name="T"/>, from a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> — which is what keeps this
    /// safe to trim and to compile ahead of time.
    /// </param>
    /// <remarks>
    /// MSDP has no types beyond text, so every leaf arrives as a string: a property that is a number
    /// or a boolean in <typeparamref name="T"/> needs
    /// <see cref="JsonSerializerOptions.NumberHandling"/> set to
    /// <see cref="System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString"/> on the
    /// context, or a converter of its own.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// The payload nests deeper than <see cref="MaxDepth"/>, or declares a variable inside an array.
    /// </exception>
    public static T? Scan<T>(IEnumerable<byte> array, Encoding encoding, JsonTypeInfo<T> typeInfo)
    {
        if (typeInfo is null) throw new ArgumentNullException(nameof(typeInfo));

        using var json = ScanToUtf8Json(array, encoding);
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    /// <summary>
    /// Scans a message and writes it as UTF-8 JSON, positioned at the start.
    /// </summary>
    private static MemoryStream ScanToUtf8Json(IEnumerable<byte> array, Encoding encoding)
    {
        var json = new MemoryStream();

        using (var writer = new Utf8JsonWriter(json))
        {
            WriteScannedValue(MSDPScan(array, encoding), writer);
        }

        json.Position = 0;
        return json;
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
    public static byte[] ReportVariables(string jsonObject, Encoding encoding) =>
        ReportVariables(JsonNode.Parse(jsonObject) as JsonObject
            ?? throw new InvalidDataException(
                "An MSDP payload is a sequence of variables, so it must be written from a JSON object."),
            encoding);

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
    public static byte[] Report(string jsonString, Encoding encoding) =>
        Report(JsonNode.Parse(jsonString), encoding);

    /// <summary>
    /// Writes a value of your own type as the payload of an MSDP subnegotiation: one
    /// <c>MSDP_VAR &lt;name&gt; MSDP_VAL &lt;value&gt;</c> pair per property.
    /// </summary>
    /// <typeparam name="T">The type describing the variables to report.</typeparam>
    /// <param name="variables">The variables to report.</param>
    /// <param name="encoding">The encoding to write text in.</param>
    /// <param name="typeInfo">
    /// The contract for <typeparamref name="T"/>, from a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> — which is what keeps this
    /// safe to trim and to compile ahead of time.
    /// </param>
    /// <exception cref="InvalidDataException"><typeparamref name="T"/> does not describe an object.</exception>
    public static byte[] ReportVariables<T>(T variables, Encoding encoding, JsonTypeInfo<T> typeInfo) =>
        ReportVariables(ToNode(variables, typeInfo) as JsonObject
            ?? throw new InvalidDataException(
                "An MSDP payload is a sequence of variables, so it must be written from a type that describes an object."),
            encoding);

    /// <summary>
    /// Writes a <see cref="JsonObject"/> as the payload of an MSDP subnegotiation.
    /// </summary>
    /// <param name="variables">The variables to report.</param>
    /// <param name="encoding">The encoding to write text in.</param>
    public static byte[] ReportVariables(JsonObject variables, Encoding encoding)
    {
        if (variables is null) throw new ArgumentNullException(nameof(variables));
        if (encoding is null) throw new ArgumentNullException(nameof(encoding));

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
    /// Writes a value of your own type as one MSDP value.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value to report.</param>
    /// <param name="encoding">The encoding to write text in.</param>
    /// <param name="typeInfo">
    /// The contract for <typeparamref name="T"/>, from a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>.
    /// </param>
    public static byte[] Report<T>(T value, Encoding encoding, JsonTypeInfo<T> typeInfo) =>
        Report(ToNode(value, typeInfo), encoding);

    /// <summary>
    /// Writes a <see cref="JsonNode"/> as one MSDP value: an object becomes a table, an array an
    /// array, anything else text.
    /// </summary>
    /// <param name="value">The value to report. <see langword="null"/> is written as <c>-1</c>.</param>
    /// <param name="encoding">The encoding to write text in.</param>
    public static byte[] Report(JsonNode? value, Encoding encoding)
    {
        if (encoding is null) throw new ArgumentNullException(nameof(encoding));

        var output = new List<byte>();
        WriteValue(value, encoding, output);
        return output.ToArray();
    }

    private static JsonNode? ToNode<T>(T value, JsonTypeInfo<T> typeInfo) =>
        typeInfo is null
            ? throw new ArgumentNullException(nameof(typeInfo))
            : JsonSerializer.SerializeToNode(value, typeInfo);

    /// <summary>
    /// Writes what <see cref="MSDPScan"/> produced as JSON. The shapes are known — a table, an array
    /// or text — so this needs no serializer and no reflection.
    /// </summary>
    private static void WriteScannedValue(object? scanned, Utf8JsonWriter writer)
    {
        switch (scanned)
        {
            case null:
                writer.WriteNullValue();
                break;

            case string text:
                writer.WriteStringValue(text);
                break;

            case IDictionary<string, object> table:
                writer.WriteStartObject();

                foreach (var entry in table)
                {
                    writer.WritePropertyName(entry.Key);
                    WriteScannedValue(entry.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case IEnumerable<object> array:
                writer.WriteStartArray();

                foreach (var item in array)
                {
                    WriteScannedValue(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                throw new InvalidDataException($"Unexpected scanned value of type {scanned.GetType()}.");
        }
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

                    // An array holds values, not variables, so a variable declared straight inside
                    // one is malformed. The payload comes from an untrusted peer, so it is refused
                    // the way the rest of malformed input is rather than with whatever exception the
                    // cast would raise.
                    if (accumulator is not SortedDictionary<string, object> table)
                    {
                        throw new InvalidDataException(
                            "An MSDP array holds values, so a variable cannot be declared inside one.");
                    }

                    table[key] = value;
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

    /// <summary>
    /// Writes text as the bytes of a variable name or value, rejecting anything that would forge a
    /// marker.
    /// </summary>
    /// <remarks>
    /// "Variables and values cannot contain the NUL, MSDP_VAL, MSDP_VAR, MSDP_TABLE_OPEN,
    /// MSDP_TABLE_CLOSE, MSDP_ARRAY_OPEN, MSDP_ARRAY_CLOSE or IAC byte." Those are bytes 0 to 6 and
    /// 255. A byte in 0–6 written into a name or value is read by the peer as structure — text
    /// containing <c>MSDP_TABLE_OPEN</c> would open a table in the middle of a value — so it is
    /// refused rather than put on the wire. <c>IAC</c> is the exception: it is legal in the encoded
    /// text of some character sets and the send path doubles it, as RFC 854 requires.
    /// </remarks>
    private static void WriteText(string text, Encoding encoding, List<byte> output)
    {
        var bytes = encoding.GetBytes(text);

        foreach (var b in bytes)
        {
            if (b <= MsdpArrayClose)
            {
                throw new InvalidDataException(
                    $"An MSDP variable or value cannot contain the NUL byte or an MSDP marker (bytes 0 to 6); \"{text}\" encodes to one at byte 0x{b:X2}.");
            }
        }

        output.AddRange(bytes);
    }

    /// <summary>
    /// Ordinal-sorted so that the JSON a caller serializes from a scan is stable.
    /// </summary>
    private static SortedDictionary<string, object> NewTable() => new(StringComparer.Ordinal);
}
