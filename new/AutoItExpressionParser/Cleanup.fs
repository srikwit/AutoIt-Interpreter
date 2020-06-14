﻿namespace Unknown6656.AutoIt3.ExpressionParser

open AST
open System


module Cleanup =
    let AsNumber =
        function
        | Null
        | False -> 0m
        | True -> 1m
        | Default -> -1m
        | Number d -> d
        | String s ->
            match Decimal.TryParse s with
            | true, d -> d
            | _ -> 0m
    
    let AsBoolean =
        function
        | Null
        | Default
        | Number 0m
        | String ""
        | False -> false
        | _ -> true

    let AsString =
        function
        | Null -> ""
        | Default -> "Default"
        | Number d -> d.ToString()
        | String s -> s
        | False -> "False"
        | True -> "True"

    let AsLiteral b = Literal <| (?) b True False

    let rec FoldConstants =
        function
        | Unary (Identity, Literal Null) -> Literal False
        | Unary (Identity, e) -> e
        | Unary (Negate, Literal Default) -> Literal Default
        | Unary (Negate, Literal Null) -> Literal Null
        | Unary (Negate, Literal l) -> 
            -(AsNumber l)
            |> Number
            |> Literal
        | Unary (Not, Literal l) -> 
            AsBoolean l
            |> not
            |> AsLiteral
        | Unary (op, e) as exp ->
            let res = Unary(op, FoldConstants e)
            (?) (res = exp) id FoldConstants <| res
        | Binary (Literal l1, op, Literal l2) as exp ->
            let num_op f = f (AsNumber l1) (AsNumber l2)
                           |> Number
                           |> Literal
            let bool_op f = AsLiteral (f (AsBoolean l1) (AsBoolean l2))
            match op, l1, l2 with
            | StringConcat, l1, l2 ->
                AsString l1
                |> (+) (AsString l2)
                |> LITERAL.String
                |> Literal
            | Or, _, _ -> bool_op (||)
            | And, _, _ -> bool_op (&&)
            | Add, _, _ -> num_op (+)
            | Subtract, _, _ -> num_op (-)
            | Multiply, _, _ -> num_op (*)
            | Divide, _, _ -> num_op (/)
            | Power, _, _ -> num_op (fun b e -> (float b) ** (float e) |> decimal)
            | EqualCaseSensitive, l1, l2 when l1 = l2 -> Literal True
            | Greater, Number d1, Number d2 -> AsLiteral (d1 > d2)
            | GreaterEqual, Number d1, Number d2 -> AsLiteral (d1 >= d2)
            | Lower, Number d1, Number d2 -> AsLiteral (d1 <= d2)
            | LowerEqual, Number d1, Number d2 -> AsLiteral (d1 <= d2)
            | _ -> exp
        | Binary (e1, op, e2) as exp ->
            let e1', e2' = (e1, e2) |>> FoldConstants
            if e1' = e1 && e2' = e2 then exp
            else
                (e1', op, e2')
                |> Binary
                |> FoldConstants
        | Ternary (a, b, c) as exp ->
            let b', c' = (b, c) |>> FoldConstants
            match FoldConstants a with
            | Literal True -> b'
            | Literal False -> c'
            | a' -> Ternary(a', b', c')
        | Member e -> Member(FoldMember e)
        | Indexer e -> Indexer(FoldIndexer e)
        | FunctionCall (DirectFunctionCall (i, a)) ->
            (i, List.map FoldConstants a)
            |> DirectFunctionCall
            |> FunctionCall
        | FunctionCall (MemberCall (e, a)) ->
            (FoldMember e, List.map FoldConstants a)
            |> MemberCall
            |> FunctionCall
        | e -> e

    and FoldIndexer e = e |>> FoldConstants

    and FoldMember =
        function
        | ExplicitMemberAccess (e, i) -> ExplicitMemberAccess(FoldConstants e, i)
        | m -> m 

    let FoldTarget =
        function
        | VariableAssignment v -> VariableAssignment v
        | IndexedAssignment i -> IndexedAssignment (FoldIndexer i)
        | MemberAssignemnt m -> MemberAssignemnt (FoldMember m)

    let FoldAssignment (target, op, ex) = (FoldTarget target, op, FoldConstants ex)
    
    let DecomposeAssignmentExpression (target, op, expr) =
        let expr = match op with
                   | AssignAdd -> Some Add
                   | AssignSubtract -> Some Subtract
                   | AssignMultiply -> Some Multiply
                   | AssignDivide -> Some Divide
                   | AssignConcat -> Some StringConcat
                   | Assign -> None
                   |> Option.map (fun bop -> 
                       let left = match target with
                                  | VariableAssignment v -> Variable v
                                  | IndexedAssignment i -> Indexer i
                                  | MemberAssignemnt m -> Member m
                       Binary (left, bop, expr))
                   |> Option.defaultValue expr
        (target, Assign, expr)

    let CleanUpExpression expression =
        match expression with
        | AnyExpression(Binary(Variable var, EqualCaseInsensitive, source)) -> (VariableAssignment var, Assign, source)
        | AnyExpression(Binary(Indexer idx, EqualCaseInsensitive, source)) -> (IndexedAssignment idx, Assign, source)
        | AnyExpression(Binary(Member membr, EqualCaseInsensitive, source)) -> (MemberAssignemnt membr, Assign, source)
        | AnyExpression e -> (VariableAssignment VARIABLE.Discard, Assign, e)
        | AssignmentExpression e -> e
        |> DecomposeAssignmentExpression
        |> FoldAssignment
        |> fun (target, op, expr) -> struct(target, op, expr)

