namespace TelnetNegotiationCore.Functional

open System.Text

type Trigger =
    | NULL = 0uy
    | MSDP_VAR = 1uy
    | MSDP_VAL = 2uy
    | MSDP_TABLE_OPEN = 3uy
    | MSDP_TABLE_CLOSE = 4uy
    | MSDP_ARRAY_OPEN = 5uy
    | MSDP_ARRAY_CLOSE = 6uy

module MSDPLibrary =
  let rec MSDPScan (root: obj, array: seq<byte>, type_: Trigger, encoding : Encoding) =
      if Seq.length(array) = 0 then root
      else
          match type_ with
          | Trigger.MSDP_VAR ->
              (root :?> Map<string, obj>).Add(encoding.GetString(array |> Seq.takeWhile(fun x -> x <> byte Trigger.MSDP_VAL) |> Array.ofSeq), MSDPScan(root, array |> Seq.skipWhile(fun x -> x <> byte Trigger.MSDP_VAL) |> Seq.skip(1), Trigger.MSDP_VAL,encoding))
          | Trigger.MSDP_VAL ->
              let nextType = 
                try
                  array |> Seq.find(fun x ->  
                  x = byte Trigger.MSDP_ARRAY_OPEN || 
                  x = byte Trigger.MSDP_TABLE_OPEN || 
                  x = byte Trigger.MSDP_ARRAY_CLOSE || 
                  x = byte Trigger.MSDP_TABLE_CLOSE || 
                  x = byte Trigger.MSDP_VAL)
                with
                | :? System.Collections.Generic.KeyNotFoundException -> 0uy

              match LanguagePrimitives.EnumOfValue nextType : Trigger with
              | Trigger.NULL -> 
                  encoding.GetString(array |> Array.ofSeq)
              | Trigger.MSDP_VAL ->
                  MSDPScan((root :?> List<obj>) @ [encoding.GetString(array |> Seq.takeWhile(fun x -> x <> nextType) |> Array.ofSeq)], array |> Seq.skipWhile(fun x -> x <> byte Trigger.MSDP_VAL) |> Seq.skip(1), Trigger.MSDP_VAL, encoding)
              | Trigger.MSDP_TABLE_CLOSE -> 
                  (root :?> List<obj>) @ [encoding.GetString(array |> Seq.takeWhile(fun x -> x <> nextType) |> Array.ofSeq)]
              | Trigger.MSDP_ARRAY_OPEN ->
                  MSDPScan(root, array |> Seq.skipWhile(fun x -> x <> byte Trigger.MSDP_ARRAY_OPEN) |> Seq.skip(1), Trigger.MSDP_ARRAY_OPEN,encoding)
              | Trigger.MSDP_TABLE_OPEN ->
                  MSDPScan(root, array |> Seq.skipWhile(fun x -> x <> byte Trigger.MSDP_TABLE_OPEN) |> Seq.skip(1), Trigger.MSDP_TABLE_OPEN,encoding)
              | _ ->  root
          | Trigger.MSDP_ARRAY_OPEN ->
              MSDPScan(List.Empty, array |> Seq.skip(1), Trigger.MSDP_VAL, encoding)
          | Trigger.MSDP_TABLE_OPEN ->
              MSDPScan(Map<string, obj> [], array |> Seq.skip(1), Trigger.MSDP_VAR, encoding)
          | _ -> failwith "Failure in MSDPScan."