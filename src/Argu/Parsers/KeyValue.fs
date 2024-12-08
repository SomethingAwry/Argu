[<AutoOpen>]
module internal Argu.KeyValueParser

open System

type KeyValueParseResult = Choice<UnionCaseParseResult [], exn>

// AppSettings parse errors are threaded to the state rather than raised directly
type KeyValueParseResults (argInfo : UnionArgInfo) =
    let emptyResult = Choice1Of2 [||]
    let results = Array.init argInfo.Cases.Value.Length (fun _ -> emptyResult)
    member _.AddResults (case : UnionCaseArgInfo) (ts : UnionCaseParseResult []) =
        results[case.Tag] <- Choice1Of2 ts

    member _.AddException (case : UnionCaseArgInfo) exn =
        results[case.Tag] <- Choice2Of2 exn

    member _.Results : KeyValueParseResult [] = results

[<NoComparison; NoEquality>]
type KeyValueParseState =
    {
        ArgInfo : UnionArgInfo
        Reader : IConfigurationReader
        Results : KeyValueParseResults
    }

/// <summary>
///     Parse single entry from key/value configuration
/// </summary>
let private parseKeyValuePartial (state : KeyValueParseState) (caseInfo : UnionCaseArgInfo) =
    let inline success ts = state.Results.AddResults caseInfo ts

    try
        match caseInfo.AppSettingsName.Value with
        | Some name ->
            match (try state.Reader.GetValue name with _ -> null) with
            | null | "" -> ()
            | entry ->
                match caseInfo.ParameterInfo.Value with
                | Primitives [||] ->
                    let ok, flag = Boolean.TryParse entry
                    if ok then
                        if flag then
                            let results = [| mkUnionCase caseInfo caseInfo.Tag ParseSource.AppSettings name [||] |]
                            success results
                    else
                        error state.ArgInfo ErrorCode.AppSettings $"AppSettings entry '%s{name}' is not <bool>."

                | Primitives fields ->
                    let tokens =
                        if fields.Length > 1 then
                            entry.Split(caseInfo.AppSettingsSeparators, caseInfo.AppSettingsSplitOptions)
                        else [| entry |]

                    let pos = ref 0
                    let parseNext (parser : FieldParserInfo) =
                        if pos.Value < tokens.Length then
                            try
                                let tok = tokens[pos.Value]
                                pos.Value <- pos.Value + 1
                                parser.Parser tok

                            with _ -> error state.ArgInfo ErrorCode.AppSettings $"AppSettings entry '%s{name}' is not <%s{parser.Description}>."
                        else
                            error state.ArgInfo ErrorCode.AppSettings $"AppSettings entry '%s{name}' missing <%s{parser.Description}> argument."

                    let parseSingleArgument() =
                        let fields = fields |> Array.map parseNext
                        mkUnionCase caseInfo caseInfo.Tag ParseSource.AppSettings name fields

                    success [| parseSingleArgument () |]

                | OptionalParam (existential, fp) ->
                    let parsed = existential.Accept { new IFunc<obj> with
                        member _.Invoke<'T>() =
                            try fp.Parser entry :?> 'T |> Some :> obj
                            with _ -> error state.ArgInfo ErrorCode.AppSettings $"AppSettings entry '%s{name}' is not <%s{fp.Description}>." }

                    let case = mkUnionCase caseInfo caseInfo.Tag ParseSource.AppSettings name [|parsed|]
                    success [|case|]

                | ListParam (existential, fp) ->
                    let tokens = entry.Split(caseInfo.AppSettingsSeparators, caseInfo.AppSettingsSplitOptions)
                    let results = existential.Accept { new IFunc<obj> with
                        member _.Invoke<'T>() =
                            tokens |> Seq.map (fun t -> fp.Parser t :?> 'T) |> Seq.toList :> _ }

                    let case = mkUnionCase caseInfo caseInfo.Tag ParseSource.AppSettings name [|results|]
                    success [|case|]

                | NullarySubCommand
                | SubCommand _ -> () // AppSettings will not handle subcommands

        | _ -> ()


    with ParseError _ as e -> state.Results.AddException caseInfo e

/// <summary>
///     Parse a given key/value configuration
/// </summary>
let parseKeyValueConfig (configReader : IConfigurationReader) (argInfo : UnionArgInfo) =
    let state = { ArgInfo = argInfo ; Reader = configReader ; Results = KeyValueParseResults(argInfo) }
    for caseInfo in argInfo.Cases.Value do parseKeyValuePartial state caseInfo
    state.Results.Results
