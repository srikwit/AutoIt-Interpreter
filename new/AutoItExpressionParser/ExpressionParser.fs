﻿namespace Unknown6656.AutoIt3.ExpressionParser

open System.Globalization
open System

open Piglet.Parser.Configuration.Generic

open AST
open Piglet.Parser.Construction
open System.Runtime.Serialization


type private Associativity =
    | Left
    | Right

type ExpressionParser() =
    inherit ParserConstructor<PARSABLE_EXPRESSION>()

    member private x.CreateTerminalF s f = x.CreateTerminal(s, fun s -> f s)
    member private x.nt_subexpr d = x.CreateNonTerminal<EXPRESSION>(sprintf "expr-%d" d)
    override x.Construct nt_result =
        let parse_num prefix (parser : string -> Int64) (input : string) =
            let s = input.TrimStart('+').ToLower().Replace(prefix, "")
            let n, s = if s.[0] = '-' then (true, s.Substring(1)) else (false, s)
            let l = parser s
            if n then -l else l
            |> Decimal
            |> Number

        x.Configurator.LexerSettings.IgnoreCase <- true
        
        (* AutoIt Expression Grammar:

                expr-stmt := assg-target assg-op value-expr
                           | value-expr

                assg-target := variable
                             | indexer-expr
                             | member-expr

                value-expr := indexer-expr
                            | member-expr
                            | regular-expr

                indexer-expr := value-expr "[" value-expr "]"

                member-expr := value-expr "." identifier
                             | "." identifier

                regular-expr := funccall
                              | value-expr "?" value-expr ":" value-expr
                              | value-expr bin-op value-expr
                              | un-op value-expr
                              | literal

                funccall := identifier "(" args ")"
                          | member-expr "(" args ")"

                args := arg-list
                      | ε

                arg-list := arg-list "," value-expr
                          | value-expr


        Binary operator precedence (ascending):
                Or
                And
                <= < > >=
                <> = ==
                &
                + -
                * /
                ^

        Unary operator precedence (ascending):
                Not
                + -
        *)


        let nt_expr_stmt            = x.CreateNonTerminal<PARSABLE_EXPRESSION>  "expr-stmt"
        let nt_assg_target          = x.CreateNonTerminal<ASSIGNMENT_TARGET>    "assg-targ"
        let nt_assg_op              = x.CreateNonTerminal<OPERATOR_ASSIGNMENT>  "assg-op"
        let nt_value_expr           = x.CreateNonTerminal<EXPRESSION>           "value-expr"
        let nt_indexer_expr         = x.CreateNonTerminal<INDEXER_EXPRESSION>   "index-expr"
        let nt_member_expr          = x.CreateNonTerminal<MEMBER_EXPRESSION>    "member-expr"
        let nt_regular_expr         = x.CreateNonTerminal<EXPRESSION>           "regular-expr"
        let nt_func_call            = x.CreateNonTerminal<FUNCCALL_EXPRESSION>  "funccall"
        let nt_binary_op            = x.CreateNonTerminal<OPERATOR_BINARY>      "bin-op"
        let nt_unary_op             = x.CreateNonTerminal<OPERATOR_UNARY>       "un-op"
        let nt_literal              = x.CreateNonTerminal<LITERAL>              "literal"
        let nt_args                 = x.CreateNonTerminal<FUNCCALL_ARGUMENTS>   "args"
        let nt_arglist              = x.CreateNonTerminal<FUNCCALL_ARGUMENTS>   "arglist"
        let t_operator_assign_add   = x.CreateTerminalF @"\+="                  (fun _ -> AssignAdd)
        let t_operator_assign_sub   = x.CreateTerminalF @"-="                   (fun _ -> AssignSubtract)
        let t_operator_assign_mul   = x.CreateTerminalF @"\*="                  (fun _ -> AssignMultiply)
        let t_operator_assign_div   = x.CreateTerminalF @"/="                   (fun _ -> AssignDivide)
        let t_operator_assign_con   = x.CreateTerminalF @"&="                   (fun _ -> AssignConcat)
        let t_operator_comp_neq     = x.CreateTerminalF @"<>"                   (fun _ -> Unequal)
        let t_operator_comp_gte     = x.CreateTerminalF @">="                   (fun _ -> GreaterEqual)
        let t_operator_comp_gt      = x.CreateTerminalF @">"                    (fun _ -> Greater)
        let t_operator_comp_lte     = x.CreateTerminalF @"<="                   (fun _ -> LowerEqual)
        let t_operator_comp_lt      = x.CreateTerminalF @"<"                    (fun _ -> Lower)
        let t_operator_comp_eq      = x.CreateTerminalF @"=="                   (fun _ -> EqualCaseSensitive)
        let t_symbol_equal          = x.CreateTerminal  @"="
        let t_symbol_questionmark   = x.CreateTerminal  @"\?"
        let t_symbol_colon          = x.CreateTerminal  @":"
        let t_symbol_dot            = x.CreateTerminal  @"\."
        let t_symbol_comma          = x.CreateTerminal  @","
        let t_symbol_minus          = x.CreateTerminal  @"-"
        let t_symbol_plus           = x.CreateTerminal  @"\+"
        let t_operator_mul          = x.CreateTerminalF @"\*"                   (fun _ -> Multiply)
        let t_operator_div          = x.CreateTerminalF @"/"                    (fun _ -> Divide)
        let t_operator_pow          = x.CreateTerminalF @"^"                    (fun _ -> Power)
        let t_operator_concat       = x.CreateTerminalF @"&"                    (fun _ -> StringConcat)
        let t_symbol_oparen         = x.CreateTerminal  @"\("
        let t_symbol_cparen         = x.CreateTerminal  @"\)"
        let t_symbol_obrack         = x.CreateTerminal  @"\["
        let t_symbol_cbrack         = x.CreateTerminal  @"\]"
     // let t_symbol_ocurly         = x.CreateTerminal  @"\{"
     // let t_symbol_ccurly         = x.CreateTerminal  @"\}"
     // let t_keyword_new           = x.CreateTerminal  @"new"
        let t_keyword_and           = x.CreateTerminalF @"and"                  (fun _ -> And)
        let t_keyword_or            = x.CreateTerminalF @"or"                   (fun _ -> Or)
        let t_keyword_not           = x.CreateTerminalF @"(not|!)"              (fun _ -> Not)
        let t_literal_true          = x.CreateTerminalF @"true"                 (fun _ -> True)
        let t_literal_false         = x.CreateTerminalF @"false"                (fun _ -> False)
        let t_literal_null          = x.CreateTerminalF @"null"                 (fun _ -> Null)
        let t_literal_default       = x.CreateTerminalF @"default"              (fun _ -> Default)
        let t_literal_empty         = x.CreateTerminalF @"empty"                (fun _ -> String "")
        let t_hex                   = x.CreateTerminalF @"(\+|-)?(0[xX][\da-fA-F]+|[\da-fA-F][hH])" (parse_num "0x" (fun s -> Int64.Parse(s.TrimEnd 'h', NumberStyles.HexNumber)))
        let t_bin                   = x.CreateTerminalF @"(\+|-)?0[bB][01]+"                        (parse_num "0b" (fun s -> Convert.ToInt64(s, 2)))
        let t_oct                   = x.CreateTerminalF @"(\+|-)?0[oO][0-7]+"                       (parse_num "0o" (fun s -> Convert.ToInt64(s, 8)))
        let t_dec                   = x.CreateTerminalF @"(\+|-)?\d+(\.\d+)?([eE](\+|-)?\d+)?"      (fun s -> match Decimal.TryParse s with
                                                                                                              | (true, d) -> d
                                                                                                              | _ -> Decimal.Parse(s, NumberStyles.Float)
                                                                                                              |> Number)
        let t_variable              = x.CreateTerminalF @"$[^\W\d]\w*"                              (fun s -> VARIABLE(s.Substring 1))
        let t_macro                 = x.CreateTerminalF @"@[^\W\d]\w*"                              (fun s -> MACRO(s.Substring 1))
        let t_string_1              = x.CreateTerminalF "\"(([^\"]*\"\"[^\"]*)*|[^\"]+)\""          (fun s -> String(s.Remove(s.Length - 1).Remove(0, 1).Replace("\"\"", "\"")))
        let t_string_2              = x.CreateTerminalF @"'(([^']*''[^']*)*|[^']+)'"                (fun s -> String(s.Remove(s.Length - 1).Remove(0, 1).Replace("''", "'")))
        let t_identifier            = x.CreateTerminalF @"([^\W\d_]|[^\W\d]\w*)"                    Identifier


        x.SetPrecedenceList (([
            AssociativityDirection.Right, [ t_symbol_questionmark; t_symbol_colon ]
            AssociativityDirection.Left, [ t_keyword_or ]
            AssociativityDirection.Left, [ t_keyword_and ]
            AssociativityDirection.Left, [ t_operator_comp_lte; t_operator_comp_lt; t_operator_comp_gt; t_operator_comp_gte ]
            AssociativityDirection.Left, [ t_operator_comp_neq; t_symbol_equal; t_operator_comp_eq ]
            AssociativityDirection.Left, [ t_operator_concat ]
            AssociativityDirection.Left, [ t_symbol_plus; t_symbol_minus ]
            AssociativityDirection.Left, [ t_operator_mul; t_operator_div ]
            AssociativityDirection.Right, [ t_operator_pow ]
        ] : list<AssociativityDirection * ITerminalWrapper list>)
        |> List.map (fun (a, g) -> struct(a, List.toArray g))
        |> List.toArray)



        reduce0 nt_result nt_expr_stmt

        reduce3 nt_expr_stmt nt_assg_target nt_assg_op nt_value_expr (fun t o e -> AssignmentExpression(t, o, e))
        reduce1 nt_expr_stmt nt_value_expr SimpleExpression

        reduce1 nt_assg_op t_symbol_equal (fun _ -> Assign)
        reduce1 nt_assg_op t_operator_assign_add (fun _ -> AssignAdd)
        reduce1 nt_assg_op t_operator_assign_sub (fun _ -> AssignSubtract)
        reduce1 nt_assg_op t_operator_assign_mul (fun _ -> AssignMultiply)
        reduce1 nt_assg_op t_operator_assign_div (fun _ -> AssignDivide)
        reduce1 nt_assg_op t_operator_assign_con (fun _ -> AssignConcat)

        reduce1 nt_assg_target t_variable VariableAssignment
        reduce1 nt_assg_target nt_indexer_expr IndexedAssignment
        reduce1 nt_assg_target nt_member_expr MemberAssignemnt

        reduce1 nt_value_expr nt_indexer_expr Indexer
        reduce1 nt_value_expr nt_member_expr Member
        reduce0 nt_value_expr nt_regular_expr

        reduce4 nt_indexer_expr nt_value_expr t_symbol_obrack nt_value_expr t_symbol_cbrack (fun e _ i _ -> (e, i))

        reduce3 nt_member_expr nt_value_expr t_symbol_dot t_identifier (fun e _ m -> ExplicitMemberAccess(e, m))
        reduce2 nt_member_expr t_symbol_dot t_identifier (fun _ m -> ImplicitMemberAccess m)

        reduce1 nt_regular_expr t_variable Variable
        reduce1 nt_regular_expr t_macro Macro
        reduce1 nt_regular_expr nt_literal Literal
        reduce1 nt_regular_expr nt_func_call FunctionCall
        // reduce7 nt_regular_expr t_symbol_oparen nt_value_expr t_symbol_questionmark nt_value_expr t_symbol_colon nt_value_expr t_symbol_cparen (fun _ a _ b _ c _ -> Ternary(a, b, c))
        reduce5 nt_regular_expr nt_value_expr t_symbol_questionmark nt_value_expr t_symbol_colon nt_value_expr (fun a _ b _ c -> Ternary(a, b, c))
        reduce3 nt_regular_expr nt_value_expr t_keyword_or nt_value_expr (fun a _ b -> Binary(a, Or, b))

        reduce3 nt_regular_expr nt_value_expr t_symbol_plus nt_value_expr (fun a _ b -> Binary(a, Add, b))

        reduce0 nt_literal t_literal_true
        reduce0 nt_literal t_literal_false
        reduce0 nt_literal t_literal_null
        reduce0 nt_literal t_literal_default
        reduce0 nt_literal t_literal_empty
        reduce0 nt_literal t_hex
        reduce0 nt_literal t_bin
        reduce0 nt_literal t_oct
        reduce0 nt_literal t_dec 
        reduce0 nt_literal t_string_1
        reduce0 nt_literal t_string_2

        reduce4 nt_func_call t_identifier t_symbol_oparen nt_args t_symbol_cparen (fun i _ a _ -> DirectFunctionCall(i, a))
        reduce4 nt_func_call nt_member_expr t_symbol_oparen nt_args t_symbol_cparen (fun m _ a _ -> MemberCall(m, a))

        reduce0 nt_args nt_arglist
        reducef nt_args (fun () -> [])
        
        reduce3 nt_arglist nt_arglist t_symbol_comma nt_value_expr (fun xs _ x -> xs@[x])
        reduce1 nt_arglist nt_value_expr (fun x -> [x])

        (*
        reduce1 nt_result nt_assignment_statement AssignmentExpression
        reduce1 nt_result nt_expression SimpleExpression

        reduce3 nt_assignment_statement nt_assignment_target nt_operator_binary_assg nt_expression (fun t o e -> (t, o, e))
        reduce1 nt_assignment_target t_variable VariableAssignment
        reduce1 nt_assignment_target nt_indexer_expression IndexedAssignment
        reduce1 nt_assignment_target nt_member_expression MemberAssignemnt

        reduce0 nt_operator_binary_assg t_operator_assign_add
        reduce0 nt_operator_binary_assg t_operator_assign_sub
        reduce0 nt_operator_binary_assg t_operator_assign_mul
        reduce0 nt_operator_binary_assg t_operator_assign_div
        reduce0 nt_operator_binary_assg t_operator_assign_con
        reduce1 nt_operator_binary_assg t_symbol_equal (fun _ -> Assign)

        reduce0 nt_literal t_literal_true
        reduce0 nt_literal t_literal_false
        reduce0 nt_literal t_literal_null
        reduce0 nt_literal t_literal_default
        reduce0 nt_literal t_literal_empty
        reduce0 nt_literal t_hex
        reduce0 nt_literal t_bin
        reduce0 nt_literal t_oct
        reduce0 nt_literal t_dec 
        reduce0 nt_literal t_string_1
        reduce0 nt_literal t_string_2


        let reduce_binary index operator_symbol associativity operator =
            let (l, r) = match associativity with
                         | Left -> (index, index + 1)
                         | Right -> (index + 1, index)
            reduce3 nt_subexpr.[index] nt_subexpr.[l] operator_symbol nt_subexpr.[r] (fun a _ b -> Binary(a, operator, b))
            reduce0 nt_subexpr.[index] nt_subexpr.[index + 1]

        let reduce_unary_prefix index operator_symbol operator =
            reduce2 nt_subexpr.[index] operator_symbol nt_subexpr.[index + 1] (fun _ e -> Unary(operator, e))
            reduce0 nt_subexpr.[index] nt_subexpr.[index + 1]

        let reduce_or_next index symbol func =
            reduce1 nt_subexpr.[index] symbol func
            reduce0 nt_subexpr.[index] nt_subexpr.[index + 1]


        // the following lines are sorted by ascending precedence
        reduce1 nt_expression nt_subexpr.[0] id
        reduce5 nt_subexpr.[0] nt_subexpr.[1] t_symbol_questionmark nt_subexpr.[1] t_symbol_colon nt_subexpr.[0] (fun a _ b _ c -> Ternary(a, b, c))
        reduce_binary 1 t_keyword_or Left Or
        reduce_binary 2 t_keyword_and Left And
        reduce_binary 3 t_operator_comp_lte Left LowerEqual
        reduce_binary 4 t_operator_comp_lt Left Lower
        reduce_binary 5 t_operator_comp_gte Left GreaterEqual
        reduce_binary 6 t_operator_comp_gt Left Greater
        reduce_binary 7 t_operator_comp_neq Left Unequal
        reduce_binary 8 t_symbol_equal Left EqualCaseInsensitive
        reduce_binary 9 t_operator_comp_eq Left EqualCaseSensitive
        reduce_binary 10 t_operator_concat Left StringConcat
        reduce_binary 11 t_symbol_minus Left Subtract
        reduce_binary 12 t_symbol_plus Left Add
        reduce_binary 13 t_operator_div Left Divide
        reduce_binary 14 t_operator_mul Left Multiply
        reduce_binary 15 t_operator_pow Right Power
        reduce_unary_prefix 16 t_keyword_not Not
        reduce_unary_prefix 17 t_symbol_minus Negate
        reduce_unary_prefix 18 t_symbol_plus Identity

        reduce4 nt_indexer_expression nt_subexpr.[19] t_symbol_obrack nt_expression t_symbol_cbrack (fun e _ i _ -> (e, i))
        reduce0 nt_subexpr.[19] nt_subexpr.[20]

        reduce3 nt_member_expression nt_subexpr.[20] t_symbol_dot t_identifier (fun e _ i -> ExplicitMemberAccess(e, i))
        reduce0 nt_subexpr.[20] nt_subexpr.[21]

        reduce_or_next 21 nt_funccall_expression FunctionCall
        reduce_or_next 22 t_variable Variable
        reduce_or_next 23 t_macro Macro
        reduce_or_next 24 nt_literal Literal

        reduce3 nt_subexpr.[25] t_symbol_oparen nt_subexpr.[0] t_symbol_cparen (fun _ e _ -> e)



        reduce3 nt_member_expression nt_expression t_symbol_dot t_identifier (fun e _ i -> ExplicitMemberAccess(e, i))
        reduce2 nt_member_expression t_symbol_dot t_identifier (fun _ i -> ImplicitMemberAccess i)

        reduce4 nt_funccall_expression t_identifier t_symbol_oparen nt_funccall_arguments t_symbol_cparen (fun i _ a _ -> DirectFunctionCall(i, a))
        // reduce4 nt_funccall_expression nt_member_expression t_symbol_oparen nt_funccall_arguments t_symbol_cparen (fun i _ a _ -> MemberCall(i, a))

        reducef nt_funccall_arguments (fun () -> [])
        reduce1 nt_funccall_arguments nt_funccall_arguments1 id
        reduce3 nt_funccall_arguments1 nt_funccall_arguments1 t_symbol_comma nt_expression (fun xs _ x -> xs@[x])
        reduce1 nt_funccall_arguments1 nt_expression (fun x -> [x])
        *)
