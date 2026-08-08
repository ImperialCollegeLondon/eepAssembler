/// An assembler for the EEP1 CPU taught at Imperial College. It watches a directory
/// and turns each '.s' or '.txt' file of EEP1 assembly language into a '.ram' file of
/// machine code that Issie can load. See README.md for how to run it.
///
/// How a file is assembled:
///   tokenize      - one source line          -> a list of Tokens
///   parse         - a list of Tokens         -> a Line (one machine code word, or none)
///   parseLines    - all the lines of a file  -> the machine code, or a list of errors
///
/// Assembly takes two passes over the file, because a jump can refer to a label defined
/// further down. Phase1 exists only to record the address of every label in the symbol
/// table; a reference to a label that has not been seen yet quietly reads as 0 rather
/// than failing. Phase2 then re-assembles from the start with every label known, and is
/// the pass whose output and diagnostics are used.
module program
open System
open EEExtensions

/// A register operand. Flags and PCX are only allowed in the special MOV forms.
type Register = Regist of int | Flags | PCX

/// Which of the two assembly passes is running: see the note at the top of this file.
type Phase = | Phase1 | Phase2

let version = "2.4"

/// What this program is actually running on, e.g. '.NET 10.0.2, x64'. It is printed at
/// startup because it answers most installation questions on sight - which .NET is really
/// being used, and whether it is the 32 or the 64 bit one - without anyone having to be
/// talked through 'dotnet --info'.
let runtimeDescription =
    let framework = Runtime.InteropServices.RuntimeInformation.FrameworkDescription
    let architecture = string Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
    $"{framework}, {architecture.ToLowerInvariant()}"

/// .NET 8 and .NET 9 both stopped receiving security updates on 10 November 2026. The
/// assembler works perfectly well on either, so this is a note and not an error - but a
/// student running one has no other way of knowing, so say it once at startup.
let noteIfRuntimeOutOfSupport() =
    if Environment.Version.Major <= 9 then
        printfn $"Note: .NET {Environment.Version.Major} stopped receiving security updates in November 2026. The assembler is"
        printfn "      happy on it, but do install a current .NET SDK when convenient:"
        printfn "      https://dotnet.microsoft.com/en-us/download"

/// The operand of an instruction, after parsing but before it is turned into bits.
type Op =
    /// shift count, 0 .. 15
    | Imm4 of int
    /// offset of a '[Rb, #n]' memory operand, -16 .. 15
    | Imm5 of int
    /// literal 8 bit operand, -128 .. 255
    | Imm8 of int
    /// a register used as the operand, e.g. 'MOV Ra, Rb'
    | RegOp of Register
    /// a '[Rb, #n]' memory operand
    | OffsetOp of Register * int
    /// a label used as an 8 bit operand; its value is not known until Phase2
    | SymImm8 of string

/// Maps each label to the address it was defined at. Lookup depends on the phase:
/// in Phase1 an unknown label is not an error, because it may be defined further down.
type SymTable =
    { Table: Map<string,int>; Phase : Phase}

        member this.Lookup name =
            match this.Phase, Map.tryFind name this.Table with
            | _, Some n -> 
                Ok n
            | Phase1, None -> 
                Ok 0
            | Phase2, None   -> 
                Error $"Can't find symbol '{name}' in symbol table"

        member this.AddSymbol name value =
            if Map.containsKey name this.Table then
                Error $"Duplicate definition of symbol '{name}'"
            else
                Ok {this with Table = Map.add name value this.Table }

        static member Initial = {Table = Map.empty; Phase = Phase1}
    

/// The bit fields of an EEP1 instruction word. Each member returns the contribution
/// one field makes to the 16 bit word, so an instruction is built by adding them up.
type IWord =
    {
        Dat: uint32}
        static member JmpCode = uint32 0xC000
        static member ExtCode = uint32 0xD000
        static member AluOpcField n = uint32 (n <<< 12)
        static member JmpOpcField n = uint32 (n <<< 9)
        static member JmpInvBit b = uint32 <| if b then (1 <<< 8) else 0
        static member Imm8Bit b = uint32 <| if b then (1 <<< 8) else 0
        static member RaField n = uint32 (n <<< 9)
        static member RbField n = uint32 (n <<< 5)
        static member RcField n = uint32 (n <<< 2)
        static member ShiftOpcField n = uint32 (((n &&& 1) <<< 4) + ((n &&& 2) <<< 8 - 1))
        static member Imm8Field n = uint32 ( n &&& 0xFF)
        static member MemOp n = uint32 0x8000u + (uint32 n <<< 12)
        static member IsThreeRegOp n =
            match n with
            | 0 | 7 | 6 -> false
            | _ -> true
        static member makeMOVC(c, a, b) =
            IWord.RcField c + IWord.RaField a + IWord.RbField b

/// One item of a source line. Every tokenised line ends with a Comment token, which
/// holds the '//' comment text, or "" when the line has no comment.
type Token =
    | ALUOP of int
    | MOVEXTRA of int
    | SHIFTOP of int
    | JMPOP of int * bool 
    | MEMOP of int
    | EXTOP
    | DCW
    | ORG
    | RETINT
    | SETI
    | CLRI
    | Imm of int 
    | Reg of Register
    | Symbol of string
    | Hash
    | LBra
    | RBra
    | Colon
    | ErrorTok of string
    | Comment of string



/// The result of assembling one source line. A Line is also the state threaded from
/// each line to the next, which is why it carries the symbol table, the phase and the
/// address as well as the output of this line.
type Line =
    {
        /// the label defined on this line, if any
        Label: string option
        /// the machine code word this line generates: None for a line that generates
        /// no code, such as a comment, a label on its own, or ORG
        Word: Result<uint32,string> option
        LineNo: int
        /// address of the NEXT word to be generated: one more than this line's own
        /// address whenever this line generates code
        Address: uint32
        Table: SymTable
        Phase: Phase
        Comment: string
        ExtMod: uint32 option
        /// Things worth telling the user about that do not stop assembly
        Warnings: string list
        /// 'label: mnemonic operands' as written in the source, with the source
        /// comment removed. Used to annotate the generated machine code.
        Annotation: string
    }
        static member First =
            {
                Label=None
                Word = None
                LineNo = 1
                Address = 0u
                Table = SymTable.Initial
                Phase = Phase1
                Comment = ""
                ExtMod = None
                Warnings = []
                Annotation = ""
            }





/// Every word or punctuation mark the assembler recognises, and the Token it becomes.
/// Source is upper-cased before lookup, so mnemonics and labels are case insensitive.
let opMap: Map<string,Token> =
    Map.ofList [
        "R0", Reg (Regist 0)
        "R1", Reg (Regist 1)
        "R2", Reg (Regist 2)
        "R3", Reg (Regist 3)
        "R4", Reg (Regist 4)
        "R5", Reg (Regist 5)
        "R6", Reg (Regist 6)
        "R7", Reg (Regist 7)
        "MOV", ALUOP 0
        "MOVC1", MOVEXTRA 1 // user-defined MOV instruction for extensions
        "MOVC2", MOVEXTRA 2 // user-defined MOV instruction for extensions
        "MOVC3", MOVEXTRA 3 // user-defined MOV instruction for extensions
        "MOVC4", MOVEXTRA 4 // user-defined MOV instruction for extensions
        "MOVC5", MOVEXTRA 5 // user-defined MOV instruction for extensions
        "MOVC6", MOVEXTRA 6 // used for interrupts
        "MOVC7", MOVEXTRA 7 // used for interrupts
        "ADD", ALUOP 1
        "SUB", ALUOP 2
        "ADC", ALUOP 3
        "SBC", ALUOP 4
        "AND", ALUOP 5
        "CMP", ALUOP 6
        "LSL", SHIFTOP 0
        "LSR", SHIFTOP 1
        "ASR", SHIFTOP 2
        "XSR", SHIFTOP 3
        "LDR", MEMOP 0
        "STR", MEMOP 2
        "NOOP", JMPOP (0,true)
        "JMP", JMPOP (0,false)
        "EXT", EXTOP
        "JNE", JMPOP (1,true)
        "JEQ", JMPOP (1,false)
        "JCS", JMPOP (2,false)
        "JCC", JMPOP (2,true)
        "JMI", JMPOP (3,false)
        "JPL", JMPOP (3,true)
        "JGE", JMPOP (4,false)
        "JLT", JMPOP (4,true)
        "JGT", JMPOP (5,false)
        "JLE", JMPOP (5,true)
        "JHI", JMPOP (6,false)
        "JLS", JMPOP (6,true)
        "JSR", JMPOP (7,false)
        "RET", JMPOP (7,true)
        "#", Hash
        "[", LBra
        "]", RBra
        ":", Colon
        "DCW", DCW
        "ORG", ORG
        "RETINT", RETINT
        "SETI", SETI
        "CLRI", CLRI
        "FLAGS", Reg Flags
        "PCX", Reg PCX
        ]


/// Render tokens back into something close to source text, for error messages
let toksToString tokL =
    let tokToS (tok:Token) =
        match tok, Map.tryPick (fun key value -> if value = tok then Some key else None) opMap with
        | _, Some key-> key
        | Symbol s, _ -> s
        | Comment "",_ -> ""
        | Comment comment, _-> "// " + comment
        | Imm n,_ -> $"#{n}"
        | _ -> sprintf "'%A'" tok
    tokL
    |> List.map tokToS
    |> String.concat  " "

/// split the input line, remove white space, return list of strings as tokens
let tokenize (s:string) =
    let s' = s.Replace("#"," # ").Replace(","," , ").Replace("["," [ ").Replace("]"," ] ").Replace(":"," : ")
    let sL =
        s'.Split([|"//"|],StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
    let s, comment =
        match sL with
        | [] -> "", Comment ""
        | [commentText] when String.startsWith "//" s' ->
            "", Comment commentText 
            
        | line :: comment -> line, Comment ((String.concat "//" comment).Trim())
    s.Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList
    |> List.map (fun s -> s.ToUpper())
    |> List.filter (fun s -> s <> "," && s <> "")
    |> List.map (fun s -> 
        match Map.tryFind s opMap with
        | Some tok -> tok
        | None when Seq.contains (s.Chars 0) "0123456789-" ->
            try Some(int32 s) with | e -> None
            |> (function | None -> ErrorTok $"can't parse {s} as an integer"                 
                         | Some n -> Imm n)
        | None when (Char.IsLetter (s.Chars 0))-> Symbol s
        | None -> ErrorTok $"'{s}' is not recognised")
    |> (fun toks -> toks @ [comment])

/// The source line as it should appear alongside the machine code it generates:
/// the assembly comment is removed, and a label is always followed by ':' whether
/// or not the source wrote one. Spelling and case of the source are kept.
let annotationOf (label: string option) (src: string) =
    let noComment =
        match src.IndexOf "//" with
        | -1 -> src
        | n -> src.Substring(0, n)
    let text = noComment.Trim()
    match label with
    | None ->
        text
    | Some lab ->
        // the label is the first token of the line, so it is this many characters
        let split = min lab.Length text.Length
        let rest =
            text.Substring split
            |> String.trimStart [|' '; '\t'|]
            |> String.trimStart [|':'|]
        $"{text.Substring(0, split)}: {rest.Trim()}"

let checkReg ra =
    match ra with
    | Regist a-> Ok a
    | ra -> Error $"{ra} is not allowed as an operand in this instruction"

let check2Regs ra rb =
    checkReg ra
    |> Result.bind (fun a -> 
        checkReg rb
        |> Result.map (fun b -> (a,b)))

let check3Regs ra rb rc =
    check2Regs ra rb
    |> Result.bind (fun (a,b) -> 
        checkReg rc
        |> Result.map (fun c -> (a,b,c)))

/// True if n fits the 8 bit immediate operand field. 128 .. 255 is allowed and
/// assembles to the same bit pattern as -128 .. -1 does: see signedImm8Warning.
let inImm8Range n = n >= -128 && n <= 255

/// 128 .. 255 and -128 .. -1 give the same 8 bit patterns, so a value written in
/// the top half of the unsigned range is read back by the CPU as a negative number.
/// This is legal, but is worth warning about in case it was not what was meant.
/// An EXT instruction supplies the high byte itself, so no sign extension happens
/// and there is then nothing to warn about.
/// The message is kept short enough to fit, with its 'line n:' prefix, on one line
/// of an 80 column terminal.
let signedImm8Warning (extMod: uint32 option) (subject: string) n =
    if extMod = None && n >= 128 && n <= 255 then
        [ $"{subject} sets the top bit, so the CPU reads it as {n - 256}" ]
    else
        []

/// Warnings for the operand of a single instruction. A symbolic jump operand is
/// assembled as an offset that has already been checked to be in -128 .. +127,
/// so only literal jump offsets can be misread as signed.
let operandWarnings (extMod: uint32 option) isJmp (symTab: SymTable) (op: Op) =
    match op with
    | Imm8 n ->
        signedImm8Warning extMod $"immediate {n}" n
    | SymImm8 s when not isJmp ->
        match symTab.Lookup s with
        | Ok n -> signedImm8Warning extMod $"symbol '{s}' = {n}" n
        | Error _ -> []
    | _ ->
        []

/// Turn one parsed operand into the bits it contributes to the instruction word.
/// 'a' is the Ra field, 'pc' the address of the instruction (needed to work out the
/// offset of a symbolic jump). The result is a function of the symbol table because
/// a label operand cannot be resolved until the table is complete.
let makeOp isJmp (pc:int) (a: int) (op:Op) =
    fun (symTab:SymTable) ->
        let ra =  IWord.RaField a
        match op with
        |  Imm4 n ->
            Ok (uint32 n)
        |  OffsetOp(Regist rb,n) ->
            Ok <| ra + IWord.RbField rb + IWord.Imm8Field (n &&& 0x1F)
        | OffsetOp(rb,_) ->
           Error $"{rb} is not allowed as an operand in this instruction"
        |  Imm8 n when inImm8Range n ->
            Ok <| ra + IWord.Imm8Field n + IWord.Imm8Bit (not isJmp)
        |  Imm8 n ->
            Error $"immediate {n} is outside -128 .. 255 - use EXT for the high byte"
        |  SymImm8 s when not isJmp->
            symTab.Lookup s
            |> Result.bind (fun n ->
                if inImm8Range n then
                    Ok (ra + IWord.Imm8Field n + IWord.Imm8Bit (not isJmp))
                else
                    Error $"symbol '{s}' = 0x%x{n} is above 255 - use EXT for the high byte")
        | SymImm8 s when symTab.Phase = Phase1 -> // if isJump
            Ok (ra) // can't error in Phase1 to allow forward references
        |  SymImm8 s -> // if isJmp
            symTab.Lookup s
            |> Result.bind (fun n ->
                let offset = n - pc
                if offset < -128 || offset > 127
                then Error $"jump to 0x%x{n} from 0x%x{pc} is outside the range -128 .. +127"
                else Ok (ra + IWord.Imm8Field offset))
        |  RegOp (r) when isJmp ->
            Error $"a jump cannot take the register operand '{r}'"
        |  RegOp(Regist r) ->
            Ok <| ra + IWord.RbField r
        | RegOp r ->
            Error $"{r} is not allowed as an operand in this instruction"
        | Imm5 n ->
            Error $"What? Imm5 operand {n} should have been turned into an offset operand"


/// 'MOV/ADD/... Ra, <operand>' - ALU operation n with a register and one operand
let makeAluOp ra n op =
    fun symTab ->
        checkReg ra
        |> Result.bind (fun a -> makeOp false 0 a op symTab)
        |> Result.map (fun w -> w + IWord.AluOpcField n)


/// 'MOVCn Ra, Rb' - one of the spare MOV encodings, selected by the Rc field
let makeMovExtraOp ra rb x =
    fun symTab ->
        check2Regs ra rb
        |> Result.bind (fun (a,b) -> 
            Ok (IWord.RaField a ||| IWord.RbField b ||| IWord.RcField x))

/// 'ADD/SUB/... Rc, Ra, Rb' - three register ALU operation, result in Rc.
/// Not every ALU operation has a three register form: see IWord.IsThreeRegOp.
let makeAluOp3 n ra rb rc =    
    fun _  ->
        check3Regs ra rb rc
        |> Result.bind (fun (a,b,c) ->
            if IWord.IsThreeRegOp n then
                Ok (IWord.RaField a ||| IWord.RbField b ||| IWord.RcField c ||| IWord.AluOpcField n)
            else
                Error $"ALU op '{n}' does not support 3 register operands")
        

/// 'LDR/STR Ra, <operand>' - memory operation n
let makeMemOp ra n op =
    fun symTab ->
            makeOp false 0 ra op symTab
            |> Result.map (fun w -> w + IWord.MemOp n)




/// 'JMP/JNE/... <operand>' - jump n, with the condition inverted when inv is true.
/// Only the low 8 bits of the operand are kept: they are the jump offset.
let makeJmpOp (inv:bool) (pc:int) ra (n:int) op =
    fun symTab ->
        makeOp true pc ra op symTab
        |> Result.map (fun w -> 
            (w &&& 255u) + IWord.JmpOpcField n + IWord.JmpInvBit inv + IWord.JmpCode)

/// 'LSL/LSR/ASR/XSR Ra, Rb, #shift' - shift n by a constant number of places
let makeShiftOp rb ra n x =
    fun _ ->
        check2Regs ra rb
        |> Result.bind (fun (a,b) ->
            match x with
            | Imm4 sCnt -> 
                Ok (IWord.RaField a ||| IWord.RbField b ||| IWord.AluOpcField 7 ||| IWord.ShiftOpcField n ||| IWord.Imm8Field sCnt)
            | _ ->
                Error $"ALU op '{n}' is not a shift op")
        
            
let (|ParseOpInner|_|) toks =
    match toks with
    | Symbol s :: rest -> 
        Some (Ok (SymImm8 s), rest)
    | Hash :: Imm n :: rest | Imm n :: rest -> 
        Some (Ok (Imm8 n), rest)
    | Reg r :: rest  -> 
        Some (Ok (RegOp r), rest)
    | toks -> Some (Error $"'{toksToString toks}' found when operand expected",[]) // nothing else matches.

let (|ParseComment|_|) comments =
    match comments with
    | [ Comment c ] -> Some (Ok c)
    | toks -> Some (Error $"'{toksToString toks}' found when comment or end-of-line expected")

let makeParse extMod (op:Result<Op,string>) (comment: Result<string,string>) =
    let addExtMod op =
        match extMod, op with   
        | None, x -> Ok x
        | Some _, SymImm8 _ -> Error "EXT cannot be used to modify a symbol - replace the symbol by a literal"
        | _, x -> Ok x
    match op,comment with
    | Error op, _ -> Some (Error op,"")
    | _, Error comment -> Some (Error comment,"")
    | Ok op, Ok comment -> Some (addExtMod op,comment)

let (|Imm4Inner|_|) tok =
    match tok with
    | Imm n when n >= 0 && n < 16 ->
        Some (Ok (Imm4 n))
    | Imm n -> 
        Some (Error $"'{toksToString [tok]}' found when shift count in range 0 to 15 expected")
    | _ -> 
        None

let (|Imm5Inner|_|) tok =
    match tok with
    | Imm n when n >= -16 && n < 16 ->
        Some (Ok (Imm5 n))
    | Imm n ->
        Some (Error $"'{toksToString [tok]}' found when memory offset in range -16 to 15 expected")
    | _ ->
        None


let (|ParseImm4|_|) toks =
    match toks with
    | Hash :: Imm4Inner n :: ParseComment c | Imm4Inner n :: ParseComment c -> 
        makeParse None n c
    | toks -> Some (Error $"'{toksToString toks}' found when integer Shift Count expected","") // nothing else matches.

let (|ParseOffsetMemOp|_|) extMod toks =
    match toks with
    | LBra:: Reg n :: Hash :: Imm5Inner imm :: RBra :: ParseComment c 
    | LBra :: Reg n :: Imm5Inner imm :: RBra :: ParseComment c -> 
                let getImm4Op = function
                    | Ok (Imm5 imm) -> Ok <| OffsetOp(n,imm)
                    | Ok x -> Error $"What? expecting Imm5, {x} should not happen"
                    | Error s -> Error s
                makeParse extMod (getImm4Op imm) c
    | _ -> None
        

let (|ParseOp|_|) extMod useBrackets toks =
    match useBrackets, toks with
    | true, LBra :: ParseOpInner (op,RBra :: ParseComment c) -> 
        makeParse extMod op c
    | _, ParseOpInner( op, ParseComment c) ->
        makeParse extMod op c
    | _ ->
        None // let the caller report this as a line that does not parse

/// Assemble one tokenised line that has had any leading label removed.
/// Returns the assembled Line, which is also the starting state for the next line.
let rec parseUnlabelled (line: Line) (tokL: Token list) : Line =
    let line = {line with Table = {line.Table with Phase = line.Phase}}
    //printfn $"Parsing {line.Phase} {line.Table.Phase} {line.Address}:'{toksToString tokL}'"
    let nl = {line with LineNo = line.LineNo + 1; ExtMod = None; Warnings = []}
    let wordOf1 (wordRes: SymTable -> Result<uint32,string>) (c: Result<string,string>)  =
        match wordRes line.Table, c with
        | Ok res, Ok comment -> 
            {nl with Word = Some (Ok res); Comment = comment}
        | _, Error c ->
            {nl with Word = Some (Error c)}
        | Error s, _->
            {nl with Word = Some (Error s)}

    let wordOf isJmp wordGen ra n op =
        match op with
        | Ok op', comment ->
            {nl with
                Word = Some (wordGen ra n op' line.Table)
                Comment = comment
                Warnings = operandWarnings line.ExtMod isJmp line.Table op'}
        | Error s, _->
            {nl with Word = Some (Error s)}
    let makeMovcInstruction c a b s = 
        {nl with Word = Some (Ok <| IWord.makeMOVC(c,a,b))}, [Comment s]
    let (|ParseOpWithExt|_|) = (|ParseOp|_|) line.ExtMod
    let (|ParseOffsetMemOpWithExt|_|) = (|ParseOffsetMemOp|_|) line.ExtMod
    let lineError s = {nl with Word = Some <| Error s}
    let error = List.tryPick (function | ErrorTok s -> Some s | _ -> None) tokL
    let tokString = toksToString tokL
    match error, tokL with
    | Some s, rest -> 
        // the line number is added when the error is reported, so it is not repeated here
        lineError $"token error: '{s}'", rest
    | _, [] ->
        // no code is generated, so any EXT modifier still applies to the next instruction
        {nl with Word = None; ExtMod = line.ExtMod}, []
    |_, [Comment s] ->
        // a comment-only or label-only line must not swallow a preceding EXT modifier
        {nl with Word = None; ExtMod = line.ExtMod}, [Comment s]
    | _, [RETINT; Comment s] -> makeMovcInstruction 6 0 0 s
    | _, [SETI; Comment s] -> makeMovcInstruction 6 0 1 s
    | _, [CLRI; Comment s] -> makeMovcInstruction 6 0 2 s
    | _, [ALUOP 0; Reg Flags; Reg (Regist a); Comment s ] -> 
        makeMovcInstruction 7 a 0 s
    | _, [ALUOP 0; Reg PCX; Reg (Regist a); Comment s ] -> 
        makeMovcInstruction 7 a 1 s
    | _, [ALUOP 0; Reg (Regist a); Reg Flags; Comment s ] -> 
        makeMovcInstruction 7 a 2 s
    | _, [ALUOP 0; Reg (Regist a); Reg PCX;  Comment s ] -> 
        makeMovcInstruction 7 a 3 s
    |_, SHIFTOP s :: Reg a :: Reg b :: ParseImm4 (op) ->
        wordOf false (makeShiftOp b) a s op, []
    | _, [ EXTOP ; Imm n ; Comment s ]
    | _, [ EXTOP ; Hash; Imm n ; Comment s ] ->
        if n >= 0 && n <= 0xFF then
            {nl with Word = Some (Ok (IWord.ExtCode + uint32 n)); ExtMod = Some (uint32 n &&& 0xFFu)}, [Comment s]
        else
            {nl with Word = Some (Error $"EXT operand {n} must be a number in range 0 .. 0xFF")}, [Comment s]
    | _, ALUOP n :: Reg rc :: Reg ra :: Reg rb :: ParseComment c ->
        wordOf1 (makeAluOp3 n ra rb rc) c, []
    | _, ALUOP n :: Reg ra :: Reg rb :: ParseComment c when n <> 0 && n <> 6 ->
        wordOf1 (makeAluOp3 n ra rb ra) c, []
    | _, ALUOP n :: Reg ra :: ParseOpWithExt false (op) ->
        wordOf false makeAluOp ra n op,[]
    | _, MOVEXTRA n :: Reg ra :: Reg rb :: ParseComment c ->
        wordOf1 (makeMovExtraOp ra rb n) c, []
    | _, [JMPOP (7,true) ; Comment s] -> // special case for RET
        {nl with Word = Some (Ok (uint32 (IWord.JmpCode + IWord.JmpOpcField 7 + IWord.JmpInvBit true)))}, [Comment s]
    | _, JMPOP (n1,inv) :: ParseOpWithExt false (op) ->
        wordOf true (makeJmpOp inv (int nl.Address)) 0 n1 op, []
    | _, MEMOP n :: Reg (Regist a) :: ParseOffsetMemOpWithExt op
    | _, MEMOP n :: Reg (Regist a) :: ParseOpWithExt true (op) ->
        wordOf false makeMemOp a n op, []
    | _, [ DCW ; Imm n ; Comment s]
    | _, [ DCW ; Hash; Imm n ; Comment s] ->
        if n >= -32768 && n <= 65535 then
            {nl with Word = Some (Ok (uint32 n &&& 0xFFFFu))}, [Comment s]
        else
            {nl with Word = Some (Error $"DCW value {n} is not in the allowed 16 bit range -32768 .. 65535")}, [Comment s]
    | _, [ORG ; Imm n;  Comment s]
    | _, [ORG ; Hash; Imm n;  Comment s] ->
        if n >= 0 && n <= 65535 then
            {nl with Address = uint32 n; Word = None}, [Comment s]
        else
            {nl with Word = Some (Error $"ORG address {n} is not in the allowed 16 bit range 0 .. 65535")}, [Comment s]
        
    | _ when line.Label.IsSome ->
        lineError $"cannot parse '{tokString}' after label '{line.Label.Value}' \
                    - a mis-spelled opcode?",[]
    | _  ->
        lineError $"cannot parse '{tokString}'"   , []
    |> (fun (line, rest) -> 
        match line, rest with
        | {Word = Some (Error msg)}, _ -> 
            line
        | _, [Comment comment] ->
            {line with Comment = comment}
        | _, [] ->
            line // the comment (if any) has already been set by wordOf / wordOf1
        | _, rest ->
            {line with Word = Some (Error (toksToString rest))})
    |> (fun line' -> 
            let usesMemory = line'.Word <> None
            //printfn $"line:{line'.LineNo}, address:{line'.Address} toks={tokString}, usesMemory={usesMemory}"
            {line' with Address = line'.Address + if usesMemory then 1u else 0u})

/// Assemble one tokenised line. A leading Symbol is a label: in Phase1 it is added to
/// the symbol table with the current address, in Phase2 the table is already complete.
/// The label may be followed by an optional ':'.
let parse (line: Line) (tokL: Token list) : Line =
    let lineError s = {line with Word = Some <| Error s; LineNo = line.LineNo + 1}
    match tokL with
    | Symbol s :: Colon :: tokL'   // 'label : mnemonic operands'
    | Symbol s :: tokL' ->        // 'label mnemonic operands'
         let table = 
             match line.Phase with
             | Phase1 -> line.Table.AddSymbol s (int line.Address)
             | Phase2 -> Ok line.Table
         match table with
         |  Error _ -> 
             lineError  $"duplicate label '{s}'"
         | Ok table' ->
             parseUnlabelled {line with Table = table'; Label = Some s} tokL'
    | _ ->
        parseUnlabelled {line with Label = None} tokL

/// runs the assembler REPL: one line of assembler in, one machine code word out.
/// Entered with the -i command line option. Quit with 'q' (or end of input).
let doLoop() =
    let rec parseLine (line:Line) : Unit =
            printf ">>"
            let txt = Console.ReadLine()
            match (if isNull txt then "q" else String.trim txt) with
            | "q" ->
                ()
            | txt ->
                String.trim txt
                |> tokenize
                |> parse line
                |> (fun line ->
                    match line.Label, line.Word with
                    | lab, Some(Ok w) ->
                        printfn "Label = %A Machine Code: 0x%04x 0b%016B" lab w w
                    | lab, None ->
                        printfn $"no output, Label = {lab}"
                    | _, Some (Error mess) ->
                        printfn $"Error: {mess}"
                    line.Warnings |> List.iter (fun w -> printfn $"Warning: {w}")
                    parseLine line)
    parseLine Line.First
 

/// Assemble a whole file. Runs both phases (see the note at the top of this file) and
/// returns either the lines that generate machine code together with any warnings, or
/// the list of errors. Errors from Phase1 stop Phase2 running.
let parseLines (txtL: string list) =
    let errorLine (line:Line) (msg:string) =
        $"line {line.LineNo - 1}: %s{msg}"

    let getErrors (lines: Line list) =
        lines
        |> List.collect (function | {Word=Some (Error msg )} as line ->
                                        [errorLine line msg]
                                  | _ -> [])

    let getWarnings (lines: Line list) =
        lines
        |> List.collect (fun line -> line.Warnings |> List.map (errorLine line))

    let parseFolder (line,outs) (srcText, tokL) =
            let parsed = parse line tokL
            let parsed = {parsed with Annotation = annotationOf parsed.Label srcText}
            // the label belongs to this line only, so it must not carry on to the next,
            // but it is kept on the stored line so the machine code can be annotated
            {parsed with Label = None}, outs @ [parsed]

    let tokLines = 
        txtL
        |> List.map tokenize

    let firstPass =
        let folder line toks =
            parse {line with Label = None} toks
        (Line.First, tokLines)
        ||> List.scan folder



    match getErrors firstPass with
    | [] -> 
        let init = {Line.First with 
                        Table = (List.last firstPass).Table; 
                        Address = 0u
                        Phase = Phase2}
        ((init,[]), List.zip txtL tokLines)
        ||> List.fold parseFolder
        |> (fun (line, outs) ->
            match getErrors outs with
            | [] ->
                // warnings are collected from Phase2 only, so they are not reported twice
                let code =
                    outs
                    |> List.filter (fun line -> line.Word <> None)
                Ok (code, getWarnings outs)
            | lst -> Error lst)
    | lst -> Error lst

/// The file extensions that hold EEP1 assembly source, upper case.
/// These are the files that watching a directory will assemble.
let sourceExtensions = [".S"; ".TXT"]

/// The extensions as shown to the user, e.g. ".s or .txt"
let sourceExtensionsText =
    sourceExtensions
    |> List.map String.toLower
    |> String.concat " or "

/// True if path names a file the assembler will assemble
let isSourceFile (path:string) =
    List.contains ((IO.Path.GetExtension path).ToUpper()) sourceExtensions

/// '1 error' but '2 errors': counts read badly without this.
let plural n (thing:string) =
    let s = if n = 1 then "" else "s"
    $"{n} {thing}{s}"

/// Assemble one file, writing 'path.ram' beside it, and report what happened.
///
/// Messages here name files and not paths: the directory being watched is printed
/// once at startup, and every file reported afterwards is in it. Each file's report
/// is one block of lines followed by a blank line, so that one run is easy to tell
/// apart from the next in a terminal that has been running all afternoon.
///
/// Errors are reported here on the console and nowhere else. Neither the source file
/// nor the '.ram' file is written to when a file fails to assemble - a student's own
/// assembly file is never edited by this program.
let assembler (path:string) =
    let formatAssembly (lines: Line list) =
        lines
        |> List.map (fun line ->
            let num = line.Address
            let word = match line.Word with | Some (Ok n) -> n | _ -> 0u
            let code = ($"0x%02x{num-1u} 0x%04x{word}").PadRight 14
            match line.Annotation with
            | "" -> code.TrimEnd()
            | text -> $"{code}// {text}")


    let name = IO.Path.GetFileName path
    let pathOut = IO.Path.ChangeExtension(path, "ram")
    let nameOut = IO.Path.GetFileName pathOut
    /// Print one run's report, then a blank line to separate it from the next run.
    let report (lines: string list) =
        lines |> List.iter (printfn "%s")
        printfn ""
    match isSourceFile path with
    | true ->
        // File IO happens on a FileSystemWatcher callback thread: an editor that still
        // holds the file open must not be allowed to bring the whole assembler down.
        try
            IO.File.ReadAllLines path
            |> Array.toList
            |> parseLines
            |> function | Error lst ->
                            // nothing is written: the '.ram' file, if there is one from
                            // an earlier run, is left alone and so is the source file
                            let errors = plural (List.length lst) "error"
                            report
                                ($"{name}: {errors}, {nameOut} not written"
                                 :: (lst |> List.map (fun e -> $"  {e}")))
                        | Ok (lst, warnings) ->
                            let output =
                                formatAssembly lst
                                |> List.toArray
                            IO.File.WriteAllLines(pathOut, output)
                            let alsoWarned =
                                match warnings with
                                | [] -> ""
                                | ws -> ", " + plural (List.length ws) "warning"
                            let written = plural output.Length "line"
                            report
                                ($"{name} -> {nameOut}: {written}{alsoWarned}"
                                 :: (warnings |> List.map (fun w -> $"  {w}")))
        with e ->
            report [$"{name}: not assembled - {e.Message}"]
    | false ->
        report [$"{name}: ignored - only {sourceExtensionsText} files are assembled"]



//------------------------------------------------------------------------//
//------------- Cross-platform (console) directory chooser ---------------//
//------------------------------------------------------------------------//

/// The assembly source files directly inside dir. Watching dir assembles
/// exactly these files - subdirectories are not watched.
let sourceFilesOf (dir:string) =
    try
        IO.Directory.EnumerateFiles dir
        |> Seq.filter isSourceFile
        |> Seq.sortBy (fun f -> (IO.Path.GetFileName f).ToUpperInvariant())
        |> Seq.toList
    with _ ->
        [] // unreadable directory: show it as empty rather than crashing

/// The subdirectories of dir worth offering, ignoring hidden ones such as .git
let subDirsOf (dir:string) =
    let isHidden (d:string) =
        try (IO.File.GetAttributes d).HasFlag IO.FileAttributes.Hidden with _ -> false
    try
        IO.Directory.EnumerateDirectories dir
        |> Seq.filter (fun d ->
            let name = IO.Path.GetFileName d
            not (name.StartsWith ".") && not (isHidden d))
        |> Seq.sortBy (fun d -> (IO.Path.GetFileName d).ToUpperInvariant())
        |> Seq.toList
    with _ ->
        []

/// Expand a leading '~' to the user's home directory, as a shell would
let expandHome (s:string) =
    let home () = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    match s with
    | "~" -> home()
    | s when s.StartsWith "~/" || s.StartsWith "~\\" ->
        IO.Path.Combine(home(), s.Substring 2)
    | s -> s

/// Interactive directory chooser that works the same on Windows, macOS and Linux.
/// It lists the assembly source files in the directory being looked at, so it is
/// clear exactly which files choosing it would watch.
/// Returns Some dir to watch, or None if the user quit.
let chooseDirectory (startDir:string) : string option =
    let rec loop (dir:string) =
        let sources = sourceFilesOf dir
        let subDirs = subDirsOf dir
        printfn ""
        printfn "=================================================================="
        printfn " Choose a directory to watch. Every %s" sourceExtensionsText
        printfn " file in the directory you choose will be assembled now, and"
        printfn " re-assembled every time you save it. Subdirectories are not watched."
        printfn "=================================================================="
        printfn ""
        printfn $"Directory: {dir}"
        printfn ""
        match sources with
        | [] ->
            printfn $"  There are no {sourceExtensionsText} files here, so nothing would be"
            printfn "  assembled until you add one."
        | _ ->
            printfn $"  Files here that would be watched ({List.length sources}):"
            sources |> List.iter (fun f -> printfn $"      {IO.Path.GetFileName f}")
        printfn ""
        if not (List.isEmpty subDirs) then
            printfn "  Subdirectories:"
            subDirs |> List.iteri (fun i d -> printfn $"   %3d{i+1}  {IO.Path.GetFileName d}")
            printfn ""
        printfn "  ENTER   watch this directory"
        if not (List.isEmpty subDirs) then
            printfn "  1..%d    go into that subdirectory" (List.length subDirs)
        printfn "  ..      go up to the parent directory"
        printfn "  <path>  go to that directory"
        printfn "  q       quit"
        printf "> "
        let input = Console.ReadLine()
        // end of input (e.g. piped or redirected) is treated as 'quit'
        match (if isNull input then "q" else input.Trim()) with
        | "q" | "Q" ->
            None
        | "" ->
            Some dir
        | ".." ->
            match IO.Path.GetDirectoryName dir with
            | null | "" ->
                printfn $"'{dir}' has no parent directory"
                loop dir
            | parent ->
                loop parent
        | entry ->
            match Int32.TryParse entry with
            | true, n when n >= 1 && n <= List.length subDirs ->
                loop subDirs[n-1]
            | true, n ->
                printfn $"There is no subdirectory numbered {n}"
                loop dir
            | _ ->
                // anything else is treated as a path, absolute or relative to dir
                let target =
                    try
                        let e = expandHome entry
                        Some (IO.Path.GetFullPath(if IO.Path.IsPathRooted e then e else IO.Path.Combine(dir, e)))
                    with _ ->
                        None
                match target with
                | Some t when IO.Directory.Exists t ->
                    loop t
                | _ ->
                    printfn $"I cannot find a directory '{entry}'"
                    loop dir
    let start =
        try IO.Path.GetFullPath startDir with _ -> IO.Directory.GetCurrentDirectory()
    loop start

//------------------------------------------------------------------------//
//--------------- Native (GUI) directory chooser -------------------------//
//------------------------------------------------------------------------//

/// What the operating system's own directory dialog did. 'Unavailable' means no dialog
/// could be shown at all - Linux without zenity or kdialog, a machine with no screen,
/// and so on - and is the signal to fall back to the console chooser above.
type NativeChoice =
    | Chose of string
    | Cancelled
    | Unavailable

/// Run 'exe' with 'args' and return its exit code and trimmed standard output.
/// None means the program could not be started at all.
let private runCapturingOutput (exe:string) (args:string list) : (int * string) option =
    try
        let psi =
            Diagnostics.ProcessStartInfo(
                exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true)
        // ArgumentList quotes each argument correctly for the platform, so paths
        // containing spaces need no escaping here
        args |> List.iter psi.ArgumentList.Add
        use p = Diagnostics.Process.Start psi
        // stderr is drained while stdout is being read: a chatty program (zenity likes
        // to print GTK warnings) could otherwise fill its stderr pipe and deadlock
        let errors = p.StandardError.ReadToEndAsync()
        let out = p.StandardOutput.ReadToEnd()
        p.WaitForExit()
        errors.Wait()
        Some (p.ExitCode, out.Trim())
    with _ ->
        None

/// Is there an executable called 'name' on the PATH?
let private onPath (name:string) =
    match Environment.GetEnvironmentVariable "PATH" with
    | null -> false
    | path ->
        path.Split IO.Path.PathSeparator
        |> Array.exists (fun dir ->
            try dir <> "" && IO.File.Exists(IO.Path.Combine(dir, name)) with _ -> false)

/// The PowerShell script that shows the standard Windows folder dialog. It writes the
/// chosen directory to standard output, writes nothing at all if the user cancels, and
/// exits with 2 if the dialog could not be shown.
let private windowsPickerScript (startDir:string) =
    // '' is how a single quote is escaped inside a PowerShell single quoted string
    let quoted = startDir.Replace("'", "''")
    String.concat "\n" [
        "try {"
        "  Add-Type -AssemblyName System.Windows.Forms"
        "  $dialog = New-Object System.Windows.Forms.FolderBrowserDialog"
        $"  $dialog.Description = 'Choose a directory to watch - every {sourceExtensionsText} file in it will be assembled'"
        "  $dialog.ShowNewFolderButton = $true"
        $"  $dialog.SelectedPath = '{quoted}'"
        // an invisible top-most owner, so the dialog opens in front of the terminal
        "  $owner = New-Object System.Windows.Forms.Form"
        "  $owner.TopMost = $true"
        "  if ($dialog.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) {"
        "    [Console]::Out.Write($dialog.SelectedPath)"
        "  }"
        "  exit 0"
        "} catch { exit 2 }"
    ]

/// Windows: the folder dialog, via the Windows PowerShell that every Windows 10 and 11
/// machine has. The script goes over as base64 so that no quoting can be mangled.
let private chooseDirectoryWindows (startDir:string) =
    let encoded =
        windowsPickerScript startDir
        |> Text.Encoding.Unicode.GetBytes   // -EncodedCommand expects UTF-16
        |> Convert.ToBase64String
    match runCapturingOutput "powershell.exe" ["-NoProfile"; "-STA"; "-EncodedCommand"; encoded] with
    | Some (0, "") -> Cancelled
    | Some (0, path) -> Chose path
    | _ -> Unavailable

/// macOS: the standard 'choose folder' dialog, via osascript, which is always installed.
let private chooseDirectoryMac (startDir:string) =
    // backslash and double quote are the only characters an AppleScript string escapes
    let quoted = startDir.Replace("\\", "\\\\").Replace("\"", "\\\"")
    let script =
        [ "try"
          $"  set chosen to (choose folder with prompt \"Choose a directory to watch\" default location POSIX file \"{quoted}\")"
          "  POSIX path of chosen"
          // cancelling raises error -128, which 'try' swallows, leaving no output
          "end try" ]
    match runCapturingOutput "osascript" (script |> List.collect (fun line -> ["-e"; line])) with
    | Some (0, "") -> Cancelled
    | Some (0, path) ->
        // 'POSIX path of' a folder ends with a separator, which we do not want
        match path.TrimEnd '/' with
        | "" -> Chose "/"
        | trimmed -> Chose trimmed
    | _ -> Unavailable

/// Linux: zenity (GNOME and most others) or kdialog (KDE) if either is installed.
/// Neither is guaranteed, and over ssh there may be no screen to put a dialog on.
let private chooseDirectoryLinux (startDir:string) =
    let hasScreen =
        ["DISPLAY"; "WAYLAND_DISPLAY"]
        |> List.exists (Environment.GetEnvironmentVariable >> String.IsNullOrEmpty >> not)
    let ran =
        if not hasScreen then None
        elif onPath "zenity" then
            runCapturingOutput "zenity"
                [ "--file-selection"
                  "--directory"
                  "--title=Choose a directory to watch"
                  // the trailing separator makes zenity start inside startDir
                  $"--filename={IO.Path.TrimEndingDirectorySeparator startDir}/" ]
        elif onPath "kdialog" then
            runCapturingOutput "kdialog"
                [ "--title"; "Choose a directory to watch"; "--getexistingdirectory"; startDir ]
        else None
    match ran with
    | Some (0, path) when path <> "" -> Chose path
    | Some (1, _) -> Cancelled   // both programs exit 1 when the user cancels
    | _ -> Unavailable

/// Show the operating system's own directory dialog, starting at 'startDir'.
/// Anything that goes wrong is reported as Unavailable, so that a machine without a
/// working dialog falls back to the console chooser instead of failing.
let chooseDirectoryNative (startDir:string) =
    try
        if OperatingSystem.IsWindows() then chooseDirectoryWindows startDir
        elif OperatingSystem.IsMacOS() then chooseDirectoryMac startDir
        elif OperatingSystem.IsLinux() then chooseDirectoryLinux startDir
        else Unavailable
    with _ ->
        Unavailable


/// Wait for a keypress, unless input has been redirected (in which case
/// there is no console to read a key from).
let waitForKey() =
    printfn "press any key to exit"
    if not Console.IsInputRedirected then
        Console.ReadKey() |> ignore

/// The directory to watch: path may be a directory, or a file inside the
/// directory to be watched. Returns None if neither of these exists.
let watchDirOf (path:string) =
    if IO.Directory.Exists path then
        Some path
    else
        // GetDirectoryName returns null for a root and "" for a bare file name
        // such as 'assem.txt', neither of which FileSystemWatcher will accept.
        let dir =
            match IO.Path.GetDirectoryName path with
            | null | "" -> IO.Directory.GetCurrentDirectory()
            | d -> d
        if IO.Directory.Exists dir then Some dir else None

/// Assemble every source file in the directory holding 'path', then keep running and
/// re-assemble each file as it is saved. Does not return until the program is killed.
let watch (path:string) =
    match watchDirOf path with
    | None ->
        printfn $"Sorry - I cannot watch '{path}': there is no such file or directory"
        waitForKey()
    | Some dir ->
        // the directory is named in full once, here: after this every message is about
        // a file in it, and names that file only
        printfn ""
        printfn $"Watching {dir}"
        printfn $"Every {sourceExtensionsText} file in it is assembled now, and again whenever it is saved."
        printfn ""
        let processFile (args: IO.FileSystemEventArgs) =
            if isSourceFile args.FullPath then
                System.Threading.Thread.Sleep 50
                assembler args.FullPath
        let sourceFiles = sourceFilesOf dir
        // e.g. prog.s and prog.txt would both be assembled into prog.ram
        sourceFiles
        |> List.groupBy (fun path -> (IO.Path.ChangeExtension(path, "ram")).ToUpperInvariant())
        |> List.filter (fun (_, files) -> List.length files > 1)
        |> List.iter (fun (_, files) ->
            let names = files |> List.map IO.Path.GetFileName |> String.concat " and "
            let ram = IO.Path.GetFileName (IO.Path.ChangeExtension(List.head files, "ram"))
            printfn $"Warning: {names} both write {ram} - one will overwrite the other"
            printfn "")
        sourceFiles
        |> List.iter assembler
        let fileSystemWatcher = new IO.FileSystemWatcher()
        fileSystemWatcher.Path <- dir
        fileSystemWatcher.NotifyFilter <- IO.NotifyFilters.LastWrite
        fileSystemWatcher.EnableRaisingEvents <- true
        fileSystemWatcher.IncludeSubdirectories <- false
        fileSystemWatcher.Changed.Add processFile
        fileSystemWatcher.Created.Add processFile
        Threading.Thread.Sleep(Threading.Timeout.Infinite)


/// Let the user pick a directory, then watch it. 'useNativeDialog' asks for the
/// operating system's own folder dialog; the console chooser is used anyway if there
/// is no dialog to be had on this machine.
let runChooser useNativeDialog =
    let start = IO.Directory.GetCurrentDirectory()
    let useConsoleChooser() =
        match chooseDirectory start with
        | Some dir -> watch dir
        | None -> printfn "No directory chosen - exiting"
    match (if useNativeDialog then chooseDirectoryNative start else Unavailable) with
    | Chose dir when IO.Directory.Exists dir -> watch dir
    | Chose dir -> printfn $"Sorry - '{dir}' is not a directory: exiting"
    | Cancelled -> printfn "No directory chosen - exiting"
    | Unavailable -> useConsoleChooser()

[<EntryPoint>]
let main argv =
    printfn $"EEP1 Assembler: Version {version} (running on {runtimeDescription})"
    noteIfRuntimeOutOfSupport()
    match argv with
    | [|"-i"|] | [|"--repl"|] ->
        printfn "Interactive mode: type one line of assembler per line, 'q' to quit"
        doLoop()
        0
    | [|"-c"|] | [|"--choose"|] ->
        runChooser true
        0
    | [|"-ct"|] | [|"--choose-text"|] ->
        runChooser false
        0
    | [||] ->
        // with no console to read from (piped or redirected input) there is no way to
        // run the chooser, so keep the old behaviour of watching the current directory
        if Console.IsInputRedirected then
            printfn "Running assembler in its own directory"
            watch "."
        else
            runChooser true
        0
    | [|pathToWatch|] ->
        watch pathToWatch
        0
    | _ ->
        printfn $"Command Line: `{Environment.CommandLine}`"
        printfn "Sorry - I do not understand this command line - expecting a single file or directory name argument"
        waitForKey()
        1





    



