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

  [<TailCall>]
  let private MSDPScanTailRec (root: obj, array: seq<byte>, encoding: Encoding) =
      let rec scan accRoot accArray =
          if Seq.length accArray = 0 then (accRoot, accArray)
          else
              match accArray |> Seq.head with
              | 1uy -> 
                  let key = encoding.GetString(accArray |> Seq.skip(1) |> Seq.takeWhile(fun x -> x <> byte Trigger.MSDP_VAL) |> Array.ofSeq)
                  let (calculatedValue, leftoverArray) = scan root (accArray |> Seq.skip(1) |> Seq.skipWhile(fun x -> x <> byte Trigger.MSDP_VAL))
                  scan ((accRoot :?> Map<string, obj>).Add(key, calculatedValue)) leftoverArray
              | 2uy ->
                  if accRoot :? Map<string, obj> then 
                      scan (Map<string, obj> []) (accArray |> Seq.skip(1))
                  elif accRoot :? List<obj> then
                      let (calculatedValue, leftoverArray) = scan (Map<string, obj> []) (accArray |> Seq.skip(1))
                      scan ((accRoot :?> List<obj>) @ [calculatedValue]) leftoverArray
                  else
                      scan accRoot (accArray |> Seq.skip(1))
              | 3uy -> 
                  scan (Map<string, obj> []) (accArray |> Seq.skip(1))
              | 5uy -> 
                  scan (List<obj>.Empty) (accArray |> Seq.skip(1))
              | 4uy | 6uy -> 
                  (accRoot, accArray |> Seq.skip(1))
              | _ -> 
                  (encoding.GetString(accArray |> Seq.takeWhile(fun x -> x > 6uy) |> Array.ofSeq), accArray |> Seq.skipWhile(fun x -> x > 6uy))
      scan root array

  let public MSDPScan(array: seq<byte>, encoding) =
    let (result, _) = MSDPScanTailRec(Map<string,obj> [], array, encoding)
    result

  let parseJsonRoot (jsonRootNode: JsonNode, encoding: Encoding) =
    let rec parseJsonValue (jsonNode: JsonNode) =
        match jsonNode.GetValueKind() with
        | JsonValueKind.Object ->
            let parsedObj =
                jsonNode.AsObject()
                |> Seq.map (fun prop ->
                    let key = prop.Key
                    let value = parseJsonValue prop.Value
                    [(byte)Trigger.MSDP_VAR] @ (encoding.GetBytes(key) |> List.ofArray) @ [(byte)Trigger.MSDP_VAL] @ value
                ) |> List.concat
            [(byte)Trigger.MSDP_TABLE_OPEN] @ parsedObj @ [(byte)Trigger.MSDP_TABLE_CLOSE]
        | JsonValueKind.Array ->
            let parsedArr =
                jsonNode.AsArray()
                |> Seq.map (fun prop -> [(byte)Trigger.MSDP_VAL] @ parseJsonValue(prop))
                |> List.ofSeq
                |> List.concat
            [(byte)Trigger.MSDP_ARRAY_OPEN] @ parsedArr @ [(byte)Trigger.MSDP_ARRAY_CLOSE]
        | JsonValueKind.String -> encoding.GetBytes(jsonNode.AsValue().ToString()) |> List.ofArray
        | JsonValueKind.Number -> encoding.GetBytes(jsonNode.AsValue().ToString()) |> List.ofArray
        | JsonValueKind.True -> encoding.GetBytes("1") |> List.ofArray
        | JsonValueKind.False -> encoding.GetBytes("0") |> List.ofArray
        | JsonValueKind.Null -> encoding.GetBytes("-1") |> List.ofArray
        | _ -> failwith "Invalid JSON value"
    parseJsonValue jsonRootNode 

  let public Report(jsonString: string, encoding: Encoding) =
    parseJsonRoot(JsonValue.Parse(jsonString), encoding) |> Array.ofList