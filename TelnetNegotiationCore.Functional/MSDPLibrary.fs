namespace TelnetNegotiationCore.Functional

open System.Text
open System.Text.Json
open System.Text.Json.Nodes

module MSDPLibrary =
    type Trigger =
        | NULL = 0uy
        | MSDP_VAR = 1uy
        | MSDP_VAL = 2uy
        | MSDP_TABLE_OPEN = 3uy
        | MSDP_TABLE_CLOSE = 4uy
        | MSDP_ARRAY_OPEN = 5uy
        | MSDP_ARRAY_CLOSE = 6uy

    let emptyMap: Map<string, obj> = Map<string, obj> []
    
    let MSDPVal: byte = byte Trigger.MSDP_VAL

    [<TailCall>]
    let rec private MSDPScanTailRec (root: obj, array: byte seq, encoding: Encoding) =
        let rec scanRec accRoot accArray =
            if Seq.length accArray = 0 then
                (accRoot, accArray)
            else
                match accArray |> Seq.head with
                | 1uy ->
                    let key =
                        encoding.GetString(
                            accArray |> Seq.skip 1 |> Seq.takeWhile (fun x -> x <> MSDPVal) |> Array.ofSeq
                        )

                    let cv, rest =
                        scanRec root (accArray |> Seq.skip 1 |> Seq.skipWhile (fun x -> x <> MSDPVal))

                    scanRec ((accRoot :?> Map<string, obj>).Add(key, cv)) rest
                | 2uy ->
                    match accRoot with
                    | :? Map<string, obj> -> scanRec emptyMap (accArray |> Seq.skip 1)
                    | :? List<obj> ->
                        let cv, rest = scanRec emptyMap (accArray |> Seq.skip 1)

                        scanRec ((accRoot :?> List<obj>) @ [ cv ]) rest
                    | _ -> scanRec accRoot (accArray |> Seq.skip 1)
                | 3uy -> scanRec emptyMap (accArray |> Seq.skip 1)
                | 4uy -> accRoot, accArray |> Seq.skip 1
                | 5uy -> scanRec [] (accArray |> Seq.skip 1)
                | 6uy -> accRoot, accArray |> Seq.skip 1
                | _ ->
                    encoding.GetString(accArray |> Seq.takeWhile (fun x -> x > 6uy) |> Array.ofSeq),
                    accArray |> Seq.skipWhile (fun x -> x > 6uy)

        scanRec root array

    let public MSDPScan (array: byte seq, encoding) =
        let result, _ = MSDPScanTailRec(emptyMap, array, encoding)
        result

    let parseJsonRoot (jsonRootNode: JsonNode, encoding: Encoding) =
        let rec parseJsonValue (jsonNode: JsonNode) =
            match jsonNode.GetValueKind() with
            | JsonValueKind.Object ->
                let parsedObj =
                    jsonNode.AsObject()
                    |> Seq.map (fun prop ->
                        [ byte Trigger.MSDP_VAR ]
                        @ (encoding.GetBytes(prop.Key) |> List.ofArray)
                        @ [ byte Trigger.MSDP_VAL ]
                        @ parseJsonValue prop.Value)
                    |> List.concat

                [ byte Trigger.MSDP_TABLE_OPEN ] @ parsedObj @ [ byte Trigger.MSDP_TABLE_CLOSE ]
            | JsonValueKind.Array ->
                let parsedArr =
                    jsonNode.AsArray()
                    |> Seq.map (fun prop -> [ byte Trigger.MSDP_VAL ] @ parseJsonValue prop)
                    |> List.ofSeq
                    |> List.concat

                [ byte Trigger.MSDP_ARRAY_OPEN ] @ parsedArr @ [ byte Trigger.MSDP_ARRAY_CLOSE ]
            | JsonValueKind.String -> encoding.GetBytes(jsonNode.AsValue().ToString()) |> List.ofArray
            | JsonValueKind.Number -> encoding.GetBytes(jsonNode.AsValue().ToString()) |> List.ofArray
            | JsonValueKind.True -> encoding.GetBytes("1") |> List.ofArray
            | JsonValueKind.False -> encoding.GetBytes("0") |> List.ofArray
            | JsonValueKind.Null -> encoding.GetBytes("-1") |> List.ofArray
            | _ -> failwith "Invalid JSON value"

        parseJsonValue jsonRootNode

    let public Report (jsonString: string, encoding: Encoding) =
        parseJsonRoot (JsonValue.Parse(jsonString), encoding) |> Array.ofList
