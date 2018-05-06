﻿module AutoItExpressionParser.Analyzer

open AutoItExpressionParser.ExpressionAST

open System


type variant = AutoItCoreLibrary.AutoItVariantType

    
let rec IsStatic =
    function
    | FunctionCall _
    | AssignmentExpression _ -> false
    | UnaryExpression (_, e) -> IsStatic e
    | ToExpression (x, y)
    | BinaryExpression (_, x, y) -> IsStatic x && IsStatic y
    | TernaryExpression (x, y, z) -> [x; y; z]
                                     |> List.map IsStatic
                                     |> List.fold (&&) true
    | VariableExpression (ArrayAccess (_, e)) -> e
                                                 |> List.map IsStatic
                                                 |> List.fold (&&) true
    | VariableExpression (DotAccess (_, m)) -> m
                                               |> List.map (function
                                                            | Method _ -> false
                                                            | Field _ -> true)
                                               |> List.fold (&&) true
    | _ -> true

let rec ProcessConstants e =
    let rec procconst e =
        let num = variant.FromDecimal >> Some
        match e with
        | Literal l ->
            match l with
            | Number d -> num d
            | False
            | Null
            | Default -> num 0m
            | True -> num 1m
            | _ -> None
        | UnaryExpression (o, Constant x) ->
            match o with
            | Identity -> Some x
            | Negate -> Some <| -x
            | Not -> Some <| variant.Not x
            | BitwiseNot -> Some <| variant.BitwiseNot x
            | StringLength -> num (decimal x.Length)
            | String1Index (_, Constant l) when variant.LowerEquals(l, variant.FromDecimal 0m) -> Some <| variant ""
            | String1Index (Constant s, Constant l) ->
                if variant.LowerEquals(l, variant.FromDecimal 0m) then variant ""
                else x.OneBasedSubstring(s, l)
                |> Some
            | _ -> None
        | BinaryExpression (o, Constant x, Constant y) ->
            let (!%) = variant.FromBool >> Some
            let (!/) f = f (x.ToDecimal()) (y.ToDecimal())
                         |> (!%)
            let (!@) f = (f >> Some) (x, y)
            let (!*) f = Some (f x y)
            match o with
            | EqualCaseSensitive -> !% variant.Equals(x, y, false)
            | EqualCaseInsensitive -> !% variant.Equals(x, y, true)
            | Unequal -> !% (not <| variant.Equals(x, y, true))
            | Greater -> !/ (>)
            | GreaterEqual -> !/ (>=)
            | Lower -> !/ (<)
            | LowerEqual -> !/ (<=)
            | Xor -> !/ (<>)
            | Nxor -> !/ (=)
            | And -> !@ variant.And
            | Nor -> !@ variant.Nor
            | Nand -> !@ variant.Nand
            | Or -> !@ variant.Or
            | Add -> !* (+)
            | Subtract -> !* (-)
            | Multiply -> !* (*)
            | Divide -> !* (/)
            | Modulus -> !* (%)
            | Power -> !@ variant.Power
            | BitwiseNand -> !@ variant.BitwiseNand
            | BitwiseAnd -> !@ variant.BitwiseAnd
            | BitwiseNxor -> !@ variant.BitwiseNxor
            | BitwiseXor -> !@ variant.BitwiseXor
            | BitwiseNor -> !@ variant.BitwiseNor
            | BitwiseOr -> !@ variant.BitwiseOr
            | BitwiseRotateLeft -> !@ variant.BitwiseRol
            | BitwiseRotateRight -> !@ variant.BitwiseRor
            | BitwiseShiftLeft -> !@ variant.BitwiseShl
            | BitwiseShiftRight -> !@ variant.BitwiseShr
            | StringConcat -> !@ variant.Concat
        | TernaryExpression (Constant x, Constant y, Constant z) -> Some (if x.ToBool() then z else y)
        | _ -> None
    and (|Constant|_|) = procconst
    let num = Number >> Literal
    match e with
    | Constant x -> num (x.ToDecimal())
    | _ ->
        let d = variant.FromDecimal
        match e with
        | UnaryExpression (Identity, x) -> ProcessConstants x
        | BinaryExpression (o, x, y) ->
            match o, x, y with
            | And, Constant c, _ when c = d 0m -> num 0m
            | And, _, Constant c when c = d 0m -> num 0m
            | And, Constant c, e when c = d 1m -> ProcessConstants e
            | And, e, Constant c when c = d 1m -> ProcessConstants e
            | And, e1, (UnaryExpression(Not, e2)) when e1 = e2 -> ProcessConstants e1
            | And, (UnaryExpression(Not, e2)), e1 when e1 = e2 -> ProcessConstants e1
            | Nand, Constant c, _ when c = d 0m -> num 1m
            | Nand, _, Constant c when c = d 0m -> num 1m
            | Or, Constant c, e when c = d 0m -> ProcessConstants e
            | Or, e, Constant c when c = d 0m -> ProcessConstants e
            | Or, Constant c, _ when c = d 1m -> num 1m
            | Or, _, Constant c when c = d 1m -> num 1m
            | Or, e1, (UnaryExpression(Not, e2)) when e1 = e2 -> ProcessConstants e1
            | Or, (UnaryExpression(Not, e2)), e1 when e1 = e2 -> ProcessConstants e1
            | Nor, Constant c, _ when c = d 1m -> num 0m
            | Nor, _, Constant c when c = d 1m -> num 0m
            | BitwiseAnd, Constant c, _ when c = d 0m -> num 0m
            | BitwiseAnd, _, Constant c when c = d 0m -> num 0m
            | BitwiseOr, Constant c, e when c = d 0m -> ProcessConstants e
            | BitwiseOr, e, Constant c when c = d 0m -> ProcessConstants e
            | BitwiseXor, Constant c, e when c = d 0m -> ProcessConstants e
            | BitwiseXor, e, Constant c when c = d 0m -> ProcessConstants e
            | BitwiseRotateLeft, Constant c, _ when c = d 0m -> num 0m
            | BitwiseRotateLeft, e, Constant c when c = d 0m -> ProcessConstants e
            | BitwiseRotateRight, Constant c, _ when c = d 0m -> num 0m
            | BitwiseRotateRight, e, Constant c when c = d 0m -> ProcessConstants e
            | BitwiseShiftLeft, Constant c, _ when c = d 0m -> num 0m
            | BitwiseShiftLeft, e, Constant c when c = d 0m -> ProcessConstants e
            | BitwiseShiftRight, Constant c, _ when c = d 0m -> num 0m
            | BitwiseShiftRight, e, Constant c when c = d 0m -> ProcessConstants e
            | Add, Constant c, e when c = d 0m -> ProcessConstants e
            | Add, e, Constant c when c = d 0m -> ProcessConstants e
            | Subtract, e, Constant c when c = d 0m -> ProcessConstants e
            | Subtract, Constant c, e when c = d 0m -> UnaryExpression(Negate, ProcessConstants e)
            | Multiply, Constant c, _ when c = d 0m -> num 0m
            | Multiply, _, Constant c when c = d 0m -> num 0m
            | Multiply, Constant c, e when c = d 1m -> ProcessConstants e
            | Multiply, e, Constant c when c = d 1m -> ProcessConstants e
            | Multiply, Constant c, e when c = d 2m -> let pc = ProcessConstants e
                                                       BinaryExpression(Add, pc, pc)
            | Multiply, e, Constant c when c = d 2m -> let pc = ProcessConstants e
                                                       BinaryExpression(Add, pc, pc)
            | Divide, e, Constant c when c = d 1m -> ProcessConstants e
            | Power, Constant c, _ when c = d 0m -> num 0m
            | Power, _, Constant c when c = d 0m -> num 1m
            | Power, e, Constant c when c = d 1m -> ProcessConstants e
            | _ ->
                let stat = IsStatic e
                let proc() =
                    let px = ProcessConstants x
                    let py = ProcessConstants y
                    if (px <> x) || (py <> y) then
                        ProcessConstants <| BinaryExpression(o, px, py)
                    else
                        e
                if stat then
                    if x = y then
                        match o with
                        | Nxor
                        | EqualCaseSensitive
                        | EqualCaseInsensitive
                        | GreaterEqual
                        | LowerEqual
                        | Divide -> num 1m
                        | Xor
                        | BitwiseXor
                        | Subtract
                        | Unequal
                        | Lower
                        | Greater
                        | Modulus -> num 0m
                        | Or
                        | And
                        | BitwiseOr
                        | BitwiseAnd -> x
                        | _ -> proc()
                    else
                        match o with
                        | Unequal -> num 1m
                        | EqualCaseSensitive
                        | EqualCaseInsensitive -> num 0m
                        | _ -> proc()
                else
                    proc()
        | TernaryExpression (x, y, z) ->
            if (y = z) || (EvaluatesToTrue x) then
                ProcessConstants y
            elif EvaluatesToFalse x then
                ProcessConstants z
            else
                let px = ProcessConstants x
                let py = ProcessConstants y
                let pz = ProcessConstants z
                if (px <> x) || (py <> y) || (pz <> z) then
                    ProcessConstants <| TernaryExpression(px, py, pz)
                else
                    e
        | FunctionCall (f, [p]) when match p with
                                     | Literal _ -> f.Equals("execute", StringComparison.InvariantCultureIgnoreCase)
                                     | _ -> false
                                     -> FunctionCall("Identity", [p])
        | FunctionCall (f, ps) -> FunctionCall(f, List.map ProcessConstants ps)
        | AssignmentExpression (o, v, e) -> AssignmentExpression(o, v, ProcessConstants e)
        | VariableExpression (ArrayAccess (v, i)) -> VariableExpression(ArrayAccess(v, List.map ProcessConstants i))
        | _ -> e

and ProcessExpression e =
    let assign_dic =
        [
            AssignAdd, Add
            AssignSubtract, Subtract
            AssignMultiply, Multiply
            AssignDivide, Divide
            AssignModulus, Modulus
            AssignConcat, StringConcat
            AssignPower, Power
            AssignNand, BitwiseNand
            AssignAnd, BitwiseAnd
            AssignNxor, BitwiseNxor
            AssignXor, BitwiseXor
            AssignNor, BitwiseNor
            AssignOr, BitwiseOr
            AssignRotateLeft, BitwiseRotateLeft
            AssignRotateRight, BitwiseRotateRight
            AssignShiftLeft, BitwiseShiftLeft
            AssignShiftRight, BitwiseShiftRight
        ]
        |> dict
    let p = ProcessConstants e
    match p with
    | AssignmentExpression (Assign, Variable v, VariableExpression (Variable w)) when v = w -> VariableExpression (Variable v)
    | AssignmentExpression (o, Variable v, e) when o <> Assign ->
        AssignmentExpression (
            Assign,
            Variable v,
            BinaryExpression (
                assign_dic.[o],
                VariableExpression (Variable v),
                ProcessConstants e
            )
        )
    // TODO
    | _ -> p

and EvaluatesTo(from, ``to``) = ProcessExpression from = ProcessExpression ``to``

and EvaluatesToFalse e = EvaluatesTo (e, Literal False)

and EvaluatesToTrue e = EvaluatesTo (e, Literal True)

    
let rec private getvarfunccalls =
    function
    | Variable _ -> []
    | DotAccess (_, m) -> List.choose (function
                                       | Method f -> Some f
                                       | _ -> None) m
    | ArrayAccess (_, i) -> i
                            |> List.map GetFunctionCallExpressions
                            |> List.concat

and GetFunctionCallExpressions (e : EXPRESSION) : FUNCCALL list =
    match e with
    | Literal _
    | Macro _ -> []
    | FunctionCall f -> [[f]]
    | VariableExpression v -> [getvarfunccalls v]
    | AssignmentExpression (_, v, i) -> [getvarfunccalls v; GetFunctionCallExpressions i]
    | UnaryExpression (_, e) -> [GetFunctionCallExpressions e]
    | ToExpression (x, y)
    | BinaryExpression (_, x, y) -> [GetFunctionCallExpressions x; GetFunctionCallExpressions y]
    | TernaryExpression (x, y, z) -> [GetFunctionCallExpressions x; GetFunctionCallExpressions y; GetFunctionCallExpressions z]
    | ArrayInitExpression xs -> List.map GetFunctionCallExpressions xs
    |> List.concat

let rec GetVariables (e : EXPRESSION) : VARIABLE list =
    let rec procvar = function
                      | Variable v
                      | DotAccess (v, _) -> [v]
                      | ArrayAccess (v, i) -> v::(List.map GetVariables i
                                                  |> List.concat)
    match e with
    | Literal _
    | Macro _ -> []
    | FunctionCall (_, es) -> []
    | VariableExpression v -> [procvar v]
    | AssignmentExpression (_, v, i) -> [procvar v; GetVariables i]
    | UnaryExpression (_, e) -> [GetVariables e]
    | ToExpression (x, y)
    | BinaryExpression (_, x, y) -> [GetVariables x; GetVariables y]
    | TernaryExpression (x, y, z) -> [GetVariables x; GetVariables y; GetVariables z]
    | ArrayInitExpression xs -> List.map GetVariables xs
    |> List.concat
