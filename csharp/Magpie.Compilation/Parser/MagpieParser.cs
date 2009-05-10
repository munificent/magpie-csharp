﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magpie.Compilation
{
    public class MagpieParser : LlParser
    {
        public static SourceFile ParseSourceFile(string filePath)
        {
            // chain the parsing passes together
            var scanner       = new Scanner(File.ReadAllText(filePath));
            var lineProcessor = new LineProcessor(scanner);
            var parser        = new MagpieParser(lineProcessor);

            return parser.SourceFile();
        }

        private MagpieParser(IEnumerable<Token> tokens) : base(tokens) {}

        // <-- Usings NamespaceContents
        private SourceFile SourceFile()
        {
            var usings = Usings();
            var contents = NamespaceContents();

            Consume(TokenType.Eof);

            return new SourceFile(usings, contents);
        }

        // <-- (USING Name LINE)*
        private IEnumerable<string> Usings()
        {
            var usings = new List<string>();

            while (ConsumeIf(TokenType.Using))
            {
                string name = Name();
                Consume(TokenType.Line);

                usings.Add(name);
            }

            return usings;
        }

        // <-- ((Namespace | Function | Struct | Union) LINE)*
        private List<object> NamespaceContents()
        {
            List<object> contents = new List<object>();

            while (true)
            {
                if      (CurrentIs(TokenType.Namespace))    contents.Add(Namespace());
                else if (CurrentIs(TokenType.Name))         contents.Add(Function());
                else if (CurrentIs(TokenType.Operator))     contents.Add(Operator());
                else if (CurrentIs(TokenType.Struct))       contents.Add(Struct());
                else if (CurrentIs(TokenType.Union))        contents.Add(Union());
                else break;

                if (!ConsumeIf(TokenType.Line)) break;
            }

            return contents;
        }

        // <-- NAMESPACE Name LINE NamespaceBody END
        private Namespace Namespace()
        {
            Consume(TokenType.Namespace);
            string name = Name();
            Consume(TokenType.Line);

            List<object> contents = NamespaceContents();
            Consume(TokenType.End);

            return new Namespace(name, contents);
        }

        // <-- NAME FunctionDefinition
        private Function Function()
        {
            string name = Consume(TokenType.Name).StringValue;
            return FunctionDefinition(name);
        }

        // <-- OPERATOR FunctionDefinition
        private Function Operator()
        {
            string name = Consume(TokenType.Operator).StringValue;
            return FunctionDefinition(name);
        }

        // <-- GenericDecl FnArgsDecl Block
        private Function FunctionDefinition(string name)
        {
            IEnumerable<Decl> typeParams = TypeArgs();

            FuncType funcType = FnArgsDecl(true);
            IUnboundExpr body = Block();

            return new Function(name, typeParams, funcType, body);
        }

        // <-- LPAREN ArgsDecl? ARROW TypeDecl? RPAREN
        private FuncType FnArgsDecl(bool includeArgNames)
        {
            Consume(TokenType.LeftParen);

            IEnumerable<ParamDecl> args = CurrentIs(TokenType.RightArrow) ? new ParamDecl[0] : ArgDecls(includeArgNames);

            Consume(TokenType.RightArrow);

            Decl returnType = CurrentIs(TokenType.RightParen) ? Decl.Unit : TypeDecl();

            Consume(TokenType.RightParen);

            return new FuncType(args, returnType);
        }

        // <-- NAME TypeDecl (COMMA NAME TypeDecl)*
        private IEnumerable<ParamDecl> ArgDecls(bool includeArgNames)
        {
            List<ParamDecl> args = new List<ParamDecl>();

            while (true)
            {
                string name = "_";
                if (includeArgNames)
                {
                    name = Consume(TokenType.Name).StringValue;
                }

                Decl type = TypeDecl();
                args.Add(new ParamDecl(name, type));

                if (!ConsumeIf(TokenType.Comma)) break;
            }

            return args;
        }

        // <-- Expression | LINE (Expression LINE)+ END
        private IUnboundExpr Block()
        {
            if (ConsumeIf(TokenType.Line))
            {
                var expressions = new List<IUnboundExpr>();

                do
                {
                    expressions.Add(Expression());
                    Consume(TokenType.Line);
                }
                while (!ConsumeIf(TokenType.End));

                return new BlockExpr(expressions);
            }
            else
            {
                return Expression();
            }
        }

        // <-- Expression LINE? | LINE (Expression LINE)+ <terminator>
        private IUnboundExpr ThenBlock(out bool hasElse)
        {
            hasElse = false;

            if (ConsumeIf(TokenType.Line))
            {
                var expressions = new List<IUnboundExpr>();

                bool inBlock = true;
                while (inBlock)
                {
                    expressions.Add(Expression());
                    Consume(TokenType.Line);

                    if (ConsumeIf(TokenType.Else))
                    {
                        inBlock = false;
                        hasElse = true;
                    }
                    else if (ConsumeIf(TokenType.End))
                    {
                        inBlock = false;
                    }
                }

                return new BlockExpr(expressions);
            }
            else
            {
                IUnboundExpr expr = Expression();

                if (ConsumeIf(TokenType.Else)) hasElse = true;
                else if (ConsumeIf(TokenType.Line, TokenType.Else)) hasElse = true;
                // for inner blocks, allow a line at the end. this is for cases like:
                // if foo then bar
                // else bang
                //
                // only do this if there is an "else" after the line so that we don't
                // eat the line after an "if/then"

                return expr;
            }
        }

        private IUnboundExpr Expression()
        {
            return DefineExpr();
        }

        // <-- (DEF | MUTABLE) NAME ASSIGN Block
        private IUnboundExpr DefineExpr()
        {
            bool? isMutable = null;

            if (ConsumeIf(TokenType.Def))
            {
                isMutable = false;
            }
            else if (ConsumeIf(TokenType.Mutable))
            {
                isMutable = true;
            }

            if (isMutable.HasValue)
            {
                string name = Consume(TokenType.Name).StringValue;
                Consume(TokenType.LeftArrow);
                IUnboundExpr body = Block();

                return new DefineExpr(name, body, isMutable.Value);
            }
            else return FlowExpr();
        }

        // <-- WHILE AssignExpr DO Block
        //   | IF AssignExpr ((THEN InnerBlock ELSE Block) | (DO Block))
        //   | AssignExpr
        private IUnboundExpr FlowExpr()
        {
            if (ConsumeIf(TokenType.While))
            {
                IUnboundExpr condition = AssignExpr();
                Consume(TokenType.Do);
                IUnboundExpr body = Block();

                return new WhileExpr(condition, body);
            }
            else if (ConsumeIf(TokenType.If))
            {
                IUnboundExpr condition = AssignExpr();

                // then/do can optionally be on the next line
                ConsumeIf(TokenType.Line);

                if (ConsumeIf(TokenType.Then))
                {
                    bool hasElse;
                    IUnboundExpr thenBody = ThenBlock(out hasElse);

                    //### bob: this is in-progress. specifically, it barfs on:
                    //
                    //  if true then Print "true 1"
                    //  
                    //  if true
                    //  then Print "true 3"
                    //  
                    //  because in both cases it consumes the newline after the 
                    //  string literals in anticipation of an "else". when the
                    //  "else" isn't found, it pops all the way up to the parent
                    //  Block(), which expects to consume a newline after an
                    //  expression. could fix this by adding look-ahead (see if
                    //  there's an "else" after the newline before consuming it)
                    //  but that makes the language LL(2). maybe there's a better
                    //  solution.

                    if (hasElse)
                    {
                        IUnboundExpr elseBody = Block();
                        return new IfThenElseExpr(condition, thenBody, elseBody);
                    }
                    else
                    {
                        return new IfThenExpr(condition, thenBody);
                    }
                }
                else
                {
                    Consume(TokenType.Do);
                    IUnboundExpr thenBody = Block();
                    return new IfThenExpr(condition, thenBody);
                }
            }
            else return AssignExpr();
        }

        // <-- TupleExpr (ASSIGN (DOT | OPERATOR | e) Block)?
        private IUnboundExpr AssignExpr()
        {
            IUnboundExpr expr = TupleExpr();

            if (ConsumeIf(TokenType.LeftArrow))
            {
                bool isDot = false;
                string opName = String.Empty;

                if (ConsumeIf(TokenType.Dot)) isDot = true;
                else if (CurrentIs(TokenType.Operator)) opName = Consume(TokenType.Operator).StringValue;

                IUnboundExpr value = Block();

                if (isDot) expr = new AssignExpr(expr, new CallExpr(value, expr));
                else if (!String.IsNullOrEmpty(opName)) expr = new AssignExpr(expr, new OperatorExpr(expr, opName, value));
                else expr = new AssignExpr(expr, value);
            }

            return expr;
        }

        // <-- OperatorExpr (COMMA OperatorExpr)*
        private IUnboundExpr TupleExpr()
        {
            var fields = new List<IUnboundExpr>();

            fields.Add(OperatorExpr());

            while (ConsumeIf(TokenType.Comma))
            {
                fields.Add(OperatorExpr());
            }

            if (fields.Count == 1) return fields[0];

            return new TupleExpr(fields);
        }

        // <-- ApplyExpr (OPERATOR ApplyExpr)*
        private IUnboundExpr OperatorExpr()
        {
            return OneOrMoreLeft(TokenType.Operator, ApplyExpr,
                (left, separator, right) => new OperatorExpr(left, separator.StringValue, right));
        }

        // <-- PrimaryExpr+
        private IUnboundExpr ApplyExpr()
        {
            return OneOrMoreRight(ReverseApplyExpr, (left, right) => new CallExpr(left, right));
        }

        // <-- ArrayExpr (DOT ArrayExpr)*
        private IUnboundExpr ReverseApplyExpr()
        {
            return OneOrMoreLeft(TokenType.Dot, ArrayExpr,
                (left, separator, right) => new CallExpr(right, left));
        }

        // <-- LBRACKET (RBRACKET PRIME TypeDecl |
        //              (OperatorExpr (COMMA OperatorExpr)* )? RBRACKET)
        //   | PrimaryExpr
        private IUnboundExpr ArrayExpr()
        {
            if (ConsumeIf(TokenType.LeftBracket))
            {
                if (ConsumeIf(TokenType.RightBracketBang))
                {
                    // empty (explicitly typed) array
                    Consume(TokenType.Prime);
                    return new ArrayExpr(TypeDecl(), true);
                }
                else if (ConsumeIf(TokenType.RightBracket))
                {
                    // empty (explicitly typed) array
                    Consume(TokenType.Prime);
                    return new ArrayExpr(TypeDecl(), false);
                }
                else
                {
                    // non-empty array
                    var elements = new List<IUnboundExpr>();

                    // get the elements
                    do
                    {
                        elements.Add(OperatorExpr());
                    }
                    while (ConsumeIf(TokenType.Comma));

                    bool isMutable = false;
                    if (ConsumeIf(TokenType.RightBracketBang))
                    {
                        isMutable = true;
                    }
                    else
                    {
                        Consume(TokenType.RightBracket);
                    }

                    return new ArrayExpr(elements, isMutable);
                }
            }
            else return PrimaryExpr();
        }

        // <-- Name
        //   | FN Name TupleType
        //   | BOOL_LITERAL
        //   | INT_LITERAL
        //   | STRING_LITERAL
        //   | LPAREN (Expression)? RPAREN
        private IUnboundExpr PrimaryExpr()
        {
            IUnboundExpr expression;

            if (CurrentIs(TokenType.Name)) expression = new NameExpr(Name(), TypeArgs());
            else if (CurrentIs(TokenType.Fn)) expression = FuncRefExpr();
            else if (CurrentIs(TokenType.Bool)) expression = new BoolExpr(Consume(TokenType.Bool).BoolValue);
            else if (CurrentIs(TokenType.Int)) expression = new IntExpr(Consume(TokenType.Int).IntValue);
            else if (CurrentIs(TokenType.String)) expression = new StringExpr(Consume(TokenType.String).StringValue);
            else if (CurrentIs(TokenType.LeftParen))
            {
                Consume(TokenType.LeftParen);

                if (CurrentIs(TokenType.RightParen))
                {
                    // () -> unit
                    expression = new UnitExpr();
                }
                else if (CurrentIs(TokenType.Operator))
                {
                    // ( OPERATOR ) -> an operator in prefix form
                    expression = new NameExpr(Consume(TokenType.Operator).StringValue);
                }
                else
                {
                    // anything else is a regular parenthesized expression
                    expression = Expression();
                }

                Consume(TokenType.RightParen);
            }
            else expression = null;

            return expression;
        }

        private IUnboundExpr FuncRefExpr()
        {
            Consume(TokenType.Fn);

            NameExpr name;
            if (CurrentIs(TokenType.Operator))
            {
                name = new NameExpr(Consume(TokenType.Operator).StringValue);
            }
            else
            {
                name = new NameExpr(Name(), TypeArgs());
            }

            return new FuncRefExpr(name, TupleType());
        }

        // <-- STRUCT NAME GenericDecl LINE StructBody END
        private Struct Struct()
        {
            Consume(TokenType.Struct);
            string name = Consume(TokenType.Name).StringValue;

            IEnumerable<Decl> typeParams = TypeArgs();
            Consume(TokenType.Line);

            List<Field> fields = StructFields();
            Consume(TokenType.End);

            return new Struct(name, typeParams, fields);
        }

        // <-- (NAME (MUTABLE?) TypeDecl LINE)*
        private List<Field> StructFields()
        {
            var fields = new List<Field>();

            while (CurrentIs(TokenType.Name))
            {
                string name = Consume(TokenType.Name).StringValue;
                bool isMutable = ConsumeIf(TokenType.Mutable);
                Decl type = TypeDecl();
                Consume(TokenType.Line);

                fields.Add(new Field(name, type, isMutable));
            }

            return fields;
        }

        // <-- UNION NAME GenericDecl LINE UnionBody END
        private Union Union()
        {
            Consume(TokenType.Union);
            string name = Consume(TokenType.Name).StringValue;

            IEnumerable<Decl> typeParams = TypeArgs();
            Consume(TokenType.Line);

            List<UnionCase> cases = UnionCases();
            Consume(TokenType.End);

            return new Union(name, typeParams, cases);
        }

        // <-- (NAME (TypeDecl)? LINE)*
        private List<UnionCase> UnionCases()
        {
            var cases = new List<UnionCase>();

            while (CurrentIs(TokenType.Name))
            {
                string name = Consume(TokenType.Name).StringValue;

                Decl type = Decl.Unit;
                if (!CurrentIs(TokenType.Line))
                {
                    type = TypeDecl();
                }
                Consume(TokenType.Line);

                cases.Add(new UnionCase(name, type));
            }

            return cases;
        }

        // <-- LPAREN (TypeDecl (COMMA TypeDecl)*)? RPAREN
        private IEnumerable<Decl> TupleType()
        {
            List<Decl> decls = new List<Decl>();

            Consume(TokenType.LeftParen);

            if (!CurrentIs(TokenType.RightParen))
            {
                decls.Add(TypeDecl());

                while (ConsumeIf(TokenType.Comma))
                {
                    decls.Add(TypeDecl());
                }
            }

            Consume(TokenType.RightParen);

            return decls;
        }

        // <-- LBRACKET TypeDecl (COMMA TypeDecl)* RBRACKET
        //   | PRIME (TypeDecl | (LPAREN TypeDecl (COMMA TypeDecl)* RPAREN))
        //   | <nothing>
        private IEnumerable<Decl> TypeArgs()
        {
            List<Decl> decls = new List<Decl>();

            // may not be any args
            if (ConsumeIf(TokenType.Prime))
            {
                if (ConsumeIf(TokenType.LeftParen))
                {
                    decls.Add(TypeDecl());

                    while (ConsumeIf(TokenType.Comma))
                    {
                        decls.Add(TypeDecl());
                    }

                    Consume(TokenType.RightParen);
                }
                else
                {
                    // no grouping, so just a single type declaration
                    decls.Add(TypeDecl());
                }
            }

            return decls;
        }

        // <-- ( LBRACKET RBRACKET PRIME ) * ( TupleType
        //                                   | FnTypeDecl
        //                                   | Name TypeArgs )
        private Decl TypeDecl()
        {
            Stack<bool> arrays = new Stack<bool>();
            while (ConsumeIf(TokenType.LeftBracket))
            {
                bool isMutable = false;
                if (ConsumeIf(TokenType.RightBracketBang))
                {
                    isMutable = true;
                }
                else
                {
                    Consume(TokenType.RightBracket);
                }

                Consume(TokenType.Prime);

                arrays.Push(isMutable);
            }

            // figure out the endmost type
            Decl type;
            if (CurrentIs(TokenType.LeftParen)) type = new TupleType(TupleType());
            else if (ConsumeIf(TokenType.Fn)) type = FnArgsDecl(false);
            else type = Decl.FromName(Name(), TypeArgs());

            // wrap it in the array declarations
            while (arrays.Count > 0)
            {
                type = new ArrayType(type, arrays.Pop());
            }

            return type;
        }

        // <-- NAME (COLON NAME)*
        private string Name()
        {
            string name = Consume(TokenType.Name).StringValue;

            while (ConsumeIf(TokenType.Colon))
            {
                name += ":" + Consume(TokenType.Name).StringValue;
            }

            return name;
        }
    }
}
