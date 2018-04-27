﻿using System.Collections.Generic;
using System;

using AutoItExpressionParser;

namespace AutoItInterpreter.PartialAST
{
    using static ExpressionAST;


    public sealed class AST_LOCAL_VARIABLE
    {
        public bool Constant { set; get; }
        public VARIABLE Variable { set; get; }
        public EXPRESSION InitExpression { set; get; }


        public override string ToString() => $"{(Constant ? "const " : "")}{Variable}{(InitExpression is null ? "" : " = " + InitExpression.Print())}";
    }

    public abstract class AST_STATEMENT
    {
        public DefinitionContext Context { set; get; }
    }

    public class AST_SCOPE
        : AST_STATEMENT
    {
        public List<AST_LOCAL_VARIABLE> ExplicitLocalVariables { get; } = new List<AST_LOCAL_VARIABLE>();
        public AST_STATEMENT[] Statements { set; get; }

        public AST_LOCAL_VARIABLE this[string name] => ExplicitLocalVariables.Find(lv => lv.Variable.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
    }

    public sealed class AST_FUNCTION
        : AST_SCOPE
    {
        public AST_FUNCTION_PARAMETER[] Parameters { set; get; }
        public string Name { set; get; }
    }

    public class AST_FUNCTION_PARAMETER
    {
        public VARIABLE Name { get; }
        public bool ByRef { get; }
        public bool Const { get; }


        public AST_FUNCTION_PARAMETER(VARIABLE var, bool bref, bool cnst)
        {
            Name = var;
            ByRef = bref;
            Const = cnst;
        }

        public override string ToString() => $"{(Const ? "const " : "")}{(ByRef ? "byref " : "")}{Name}";
    }

    public sealed class AST_FUNCTION_PARAMETER_OPT
        : AST_FUNCTION_PARAMETER
    {
        public EXPRESSION InitExpression { get; }


        public AST_FUNCTION_PARAMETER_OPT(VARIABLE var, EXPRESSION initexpr)
            : base(var, false, false) => InitExpression = initexpr;

        public override string ToString() => $"{base.ToString()} = {InitExpression.Print()}";
    }

    public sealed class AST_ASSIGNMENT_STATEMNT
        : AST_STATEMENT
    {
        public ASSIGNMENT_EXPRESSION Expression { set; get; }
    }

    public sealed class AST_IF_STATEMENT
        : AST_STATEMENT
    {
        public AST_CONDITIONAL_BLOCK If { set; get; }
        public AST_CONDITIONAL_BLOCK[] ElseIf { set; get; }
        public AST_STATEMENT[] OptionalElse { set; get; }
    }

    public class AST_CONDITIONAL_BLOCK
        : AST_SCOPE
    {
        public EXPRESSION Condition { set; get; }
    }

    public sealed class AST_WHILE_STATEMENT
        : AST_STATEMENT
    {
        public AST_CONDITIONAL_BLOCK WhileBlock { set; get; }
        public AST_LABEL ContinueLabel { set; get; }
        public AST_LABEL ExitLabel { set; get; }


        public static implicit operator AST_WHILE_STATEMENT(AST_CONDITIONAL_BLOCK b) => new AST_WHILE_STATEMENT { WhileBlock = b };
    }

    public sealed class AST_WITH_STATEMENT
        : AST_STATEMENT
    {
        public EXPRESSION WithExpression { set; get; }
        public AST_WITH_LINE[] WithLines { set; get; }
    }

    // TODO
    public sealed class AST_WITH_LINE
        : AST_STATEMENT
    {
        public dynamic Expression { set; get; }
    }

    public sealed class AST_LABEL
        : AST_STATEMENT
    {
        private static long _tmp = 0L;

        public string Name { set; get; }

        public static AST_LABEL NewLabel => new AST_LABEL { Name = $"__lb<>{++_tmp:x4}" };
    }

    public sealed class AST_GOTO_STATEMENT
        : AST_STATEMENT
    {
        public AST_LABEL Label { set; get; }
    }

    public sealed class AST_SWITCH_STATEMENT
        : AST_STATEMENT
    {
        public AST_SWITCH_CASE[] Cases { set; get; }
        public EXPRESSION Expression { get; set; }
    }

    public abstract class AST_SWITCH_CASE
        : AST_SCOPE
    {
    }

    public sealed class AST_SWITCH_CASE_EXPRESSION
        : AST_SWITCH_CASE
    {
        public MULTI_EXPRESSION[] Expressions { set; get; }
    }

    public sealed class AST_SWITCH_CASE_ELSE
        : AST_SWITCH_CASE
    {
    }

    public sealed class AST_SELECT_CASE
        : AST_STATEMENT
    {
        public AST_CONDITIONAL_BLOCK CaseBlock { set; get; }


        public static implicit operator AST_SELECT_CASE(AST_CONDITIONAL_BLOCK b) => new AST_SELECT_CASE { CaseBlock = b };
    }

    public abstract class AST_EXPR_STATEMENT
        : AST_STATEMENT
    {
    }

    public sealed class AST_EXPRESSION_STATEMENT
        : AST_EXPR_STATEMENT
    {
        public EXPRESSION Expression { set; get; }
    }

    public sealed class AST_ASSIGNMENT_EXPRESSION_STATEMENT
        : AST_EXPR_STATEMENT
    {
        public ASSIGNMENT_EXPRESSION Expression { set; get; }
    }

    public sealed class AST_CONTINUECASE_STATEMENT
        : AST_STATEMENT
    {
    }

    public class AST_RETURN_STATEMENT
        : AST_STATEMENT
    {
    }

    public sealed class AST_RETURN_VALUE_STATEMENT
        : AST_RETURN_STATEMENT
    {
        public EXPRESSION Expression { set; get; }
    }

    public sealed class AST_BREAK_STATEMENT
        : AST_STATEMENT
    {
        public uint Level { set; get; }
    }

    public sealed class AST_CONTINUE_STATEMENT
        : AST_STATEMENT
    {
        public uint Level { set; get; }
    }

    public sealed class AST_REDIM_STATEMENT
        : AST_STATEMENT
    {
        public VARIABLE Variable { set; get; }
        public EXPRESSION[] DimensionExpressions { set; get; }
    }

    public sealed class AST_DECLARATION_STATEMENT
        : AST_STATEMENT
    {
        public VARIABLE Variable { set; get; }
        public EXPRESSION InitExpression { set; get; }
        public EXPRESSION[] DimensionExpressions { set; get; }
    }

    public sealed class AST_INLINE_CSHARP
        : AST_STATEMENT
    {
        public string Code { set; get; }
    }
}
