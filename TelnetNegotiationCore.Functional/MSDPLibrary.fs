namespace TelnetNegotiationCore.Functional

open System.Text

module MSDPLibrary =
  type Trigger =
      | NULL = 0uy
      | MSDP_VAR = 1uy
      | MSDP_VAL = 2uy
      | MSDP_TABLE_OPEN = 3uy
      | MSDP_TABLE_CLOSE = 4uy
      | MSDP_ARRAY_OPEN = 5uy
      | MSDP_ARRAY_CLOSE = 6uy

  let rec private MSDPScanRec (root: obj, array: seq<byte>, encoding : Encoding) =
      if Seq.length(array) = 0 then (root, array)
      else
          match array |> Seq.head with
          | 1uy -> 
            let key = encoding.GetString(array |> Seq.skip(1) |> Seq.takeWhile(fun x -> x <> byte Trigger.MSDP_VAL) |> Array.ofSeq)
            let (calculatedValue, leftoverArray) = MSDPScanRec(root, array |> Seq.skip(1) |> Seq.skipWhile(fun x -> x <> byte Trigger.MSDP_VAL), encoding)
            MSDPScanRec((root :?> Map<string, obj>).Add(key, calculatedValue), leftoverArray, encoding)
          | 2uy ->
              if root :? Map<string,obj> then 
                MSDPScanRec(Map<string,obj> [], array |> Seq.skip(1), encoding)
              elif root :? List<obj> then
                let (calculatedValue, leftoverArray) = MSDPScanRec(Map<string,obj> [], array |> Seq.skip(1), encoding)
                MSDPScanRec((root :?> List<obj>) @ [calculatedValue], leftoverArray, encoding)
              else
                MSDPScanRec(root, array |> Seq.skip(1), encoding)
          | 3uy -> 
              MSDPScanRec(Map<string,obj> [], array |> Seq.skip(1), encoding)
          | 4uy -> 
              (
                root, 
                array |> Seq.skip(1)
              )
          | 5uy -> 
              MSDPScanRec(List<obj>.Empty, array |> Seq.skip(1), encoding)
          | 6uy -> 
              (
                root, 
                array |> Seq.skip(1)
              )
          | _ -> 
            let result = encoding.GetString(array |> Seq.takeWhile(fun x -> x > byte Trigger.MSDP_ARRAY_CLOSE) |> Array.ofSeq)
            (
              result,
              array |> Seq.skipWhile(fun x -> x > byte Trigger.MSDP_ARRAY_CLOSE)
            )

  let public MSDPScan(array: seq<byte>, encoding) =
    let a = [byte Trigger.MSDP_ARRAY_CLOSE] |> Seq.append array |> Seq.append [byte Trigger.MSDP_VAL; byte Trigger.MSDP_TABLE_OPEN]
    let (result, _) = MSDPScanRec(null, a, encoding)
    result