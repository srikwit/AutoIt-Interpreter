﻿using System.Collections.Generic;

using AutoItExpressionParser;

namespace AutoItInterpreter.PartialAST
{
    using static ExpressionAST;


    public sealed class AST_LOCAL_VARIABLE
    {
        public VARIABLE Variable { set; get; }
        public EXPRESSION InitExpression { set; get; }
    }

    public abstract class AST_STATEMENT
    {
        public DefinitionContext Context { set; get; }
    }

    public class AST_SCOPE
        : AST_STATEMENT
    {
        public List<AST_LOCAL_VARIABLE> ExplicitLocalsVariables { get; } = new List<AST_LOCAL_VARIABLE>();
        public AST_STATEMENT[] Statements { set; get; }
    }

    public sealed class AST_FUNCTION
        : AST_SCOPE
    {
        public string Name { set; get; }
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


        public static implicit operator AST_WHILE_STATEMENT(AST_CONDITIONAL_BLOCK b) => new AST_WHILE_STATEMENT { WhileBlock = b };
    }

    public sealed class AST_DO_STATEMENT
        : AST_STATEMENT
    {
        public AST_CONDITIONAL_BLOCK DoBlock { set; get; }


        public static implicit operator AST_DO_STATEMENT(AST_CONDITIONAL_BLOCK b) => new AST_DO_STATEMENT { DoBlock = b };
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

    public sealed class AST_SELECT_STATEMENT
        : AST_STATEMENT
    {
        public AST_SELECT_CASE[] Cases { set; get; }
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

    public sealed class AST_SWITCH_CASE_SINGLEVALUE
        : AST_SWITCH_CASE
    {
        public EXPRESSION Value { set; get; }
    }

    public sealed class AST_SWITCH_CASE_RANGE
        : AST_SWITCH_CASE
    {
        public EXPRESSION Infimum { set; get; }
        public EXPRESSION Supremum { set; get; }
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

    public class AST_BREAK_STATEMENT
        : AST_STATEMENT
    {
        public uint Level { set; get; }
    }

    public class AST_CONTINUE_STATEMENT
        : AST_STATEMENT
    {
        public uint Level { set; get; }
    }
}