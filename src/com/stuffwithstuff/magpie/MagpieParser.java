package com.stuffwithstuff.magpie;

import java.util.*;
import com.stuffwithstuff.magpie.ast.*;
import com.stuffwithstuff.magpie.type.*;

public class MagpieParser extends Parser {
  public MagpieParser(Lexer lexer) {
    super(lexer);
  }
  
  public SourceFile parse() {
    SourceFile file = sourceFile();
    
    // Make sure we didn't stop early.
    consume(TokenType.EOF);
    return file;
  }
  
  private SourceFile sourceFile() {
    List<FunctionDefn> functions = new ArrayList<FunctionDefn>();
    
    while (lookAhead(TokenType.NAME)) {
      functions.add(function());
    }
    
    return new SourceFile(functions);
  }
  
  private FunctionDefn function() {
    String name = consume(TokenType.NAME).getString();
    
    List<String> paramNames = new ArrayList<String>();
    FunctionType type = functionType(paramNames);
    
    Expr body = parseBlock();
    consume(TokenType.LINE);
    
    return new FunctionDefn(name, type, paramNames, body);
  }
  
  private FunctionType functionType(List<String> paramNames) {
    // Parse the prototype: (foo Foo, bar Bar -> Bang)
    // TODO(bob): Hack. Right now it doesn't support args or returns.
    consume(TokenType.LEFT_PAREN);
    
    TypeDecl paramType = null;
    if (match(TokenType.ARROW)) {
      // No parameters.
      paramType = new NamedType("Unit");
    } else {
      // Parse all of the parameters.
      List<TypeDecl> paramTypes = new ArrayList<TypeDecl>();
      
      do {
        // If we're looking for parameter names, parse it now.
        if (paramNames != null) {
          paramNames.add(consume(TokenType.NAME).getString());
        }
        
        // Parse the type.
        paramTypes.add(typeDeclaration());
      } while (match(TokenType.COMMA));
      
      if (paramTypes.size() == 1) {
        paramType = paramTypes.get(0);
      } else {
        // Multiple parameters, so make a tuple.
        paramType = new TupleType(paramTypes);
      }
      
      consume(TokenType.ARROW);
    }
    
    // Parse the return type, if any.
    TypeDecl returnType = null;
    if (match(TokenType.RIGHT_PAREN)) {
      // No return type, so infer Unit.
      returnType = new NamedType("Unit");
    } else {
      returnType = typeDeclaration();
      consume(TokenType.RIGHT_PAREN);
    }
    
    return new FunctionType(paramType, returnType);
  }
  
  private TypeDecl typeDeclaration() {
    // TODO(bob): Support more than just named types.
    String name = consume(TokenType.NAME).getString();
    return new NamedType(name);
  }
  
  private Expr expression() {
    return definition();
  }
  
  private Expr definition() {
    if (match(TokenType.DEF) || match(TokenType.VAR)) {
      boolean isMutable = last(1).getType() == TokenType.VAR;
      
      // TODO(bob): support multiple definitions and tuple decomposition here
      String name = consume(TokenType.NAME).getString();
      consume(TokenType.EQUALS);
      
      // Skip over assignment to prevent "def a = b = c"
      Expr value = flowControl();
      return new DefineExpr(isMutable, name, value);
    }
    else return assignment();
  }

  /* TODO(bob): Need to figure out how the syntax for assignment is going to be
   * parsed. A lot of things can be on the left side of =, but equally many
   * can't. The full breakdown is:
   * 
   * Allowed:                                 Desugared
   * --------------------------------------------------------------------------
   * Name         foo          = 123          Implemented natively
   * Method       foo.bar      = 123          foo.bar=(123)
   *              foo.bar 456  = 123          foo.bar=(456, 123)
   *              foo.456      = 123          foo.(Int)=(456, 123)
   * Operator     foo ?! 456   = 123          foo.?!=(456, 123)
   * Tuple        foo, foo.bar = 123, 456     foo = 123; foo.bar=(456)
   * (where each field must be allowed on the left side)
   * 
   * The following are not allowed to the left of a =:
   * Literals like 123, "foo", ().
   * Calls like "123 abs".
   * Flow control, definition, or other expressions.
   */
  private Expr assignment() {
    // TODO(bob): Support more than just names.
    if (match(TokenType.NAME, TokenType.EQUALS)) {
      String name = last(2).getString();
      Expr value = flowControl();
      return new AssignExpr(name, value);
    }
    else return flowControl();
  }
  
  private Expr flowControl() {
    if (match(TokenType.DO)) {
      // "do" block.
      return parseBlock();
    } else if (lookAhead(TokenType.IF)) {
      
      // Parse the conditions.
      List<Expr> conditions = new ArrayList<Expr>();
      while (match(TokenType.IF)) {
        conditions.add(parseIfBlock());
      }
      
      // Parse the then body.
      consume(TokenType.THEN);
      Expr thenExpr = parseThenBlock();
      
      // Parse the else body.
      Expr elseExpr = null;
      if (match(TokenType.ELSE) || match(TokenType.LINE, TokenType.ELSE)) {
        elseExpr = parseElseBlock();
      } else {
        elseExpr = new NothingExpr();
      }
      
      return new IfExpr(conditions, thenExpr, elseExpr);
    }
    else return tuple();
  }
  
  // TODO(bob): There's a lot of overlap in the next four functions, but,
  //            unfortunately also some slight differences. It would be cool to
  //            unify these somehow.
  
  private Expr parseBlock() {
    if (match(TokenType.LINE)){
      List<Expr> exprs = new ArrayList<Expr>();
      
      do {
        exprs.add(expression());
        consume(TokenType.LINE);
      } while (!match(TokenType.END));
            
      return new BlockExpr(exprs);
    } else {
      return expression();
    }
  }

  private Expr parseIfBlock() {
    if (match(TokenType.LINE)){
      List<Expr> exprs = new ArrayList<Expr>();
      
      do {
        exprs.add(expression());
        consume(TokenType.LINE);
      } while (!lookAhead(TokenType.THEN));
      
      consume(TokenType.LINE);

      return new BlockExpr(exprs);
    } else {
      Expr expr = expression();
      // Each if expression may be on its own line.
      match(TokenType.LINE);
      return expr;
    }
  }

  private Expr parseThenBlock() {
    if (match(TokenType.LINE)){
      List<Expr> exprs = new ArrayList<Expr>();
      
      do {
        exprs.add(expression());
        consume(TokenType.LINE);
      } while (!lookAhead(TokenType.ELSE) && !match(TokenType.END));
      
      if (lookAhead(TokenType.ELSE)) {
        // A newline is allowed before else
        match(TokenType.LINE);
      } else {
        // A newline is required after end
        consume(TokenType.LINE);
      }
      
      return new BlockExpr(exprs);
    } else {
      return expression();
    }
  }
  
  private Expr parseElseBlock() {
    if (match(TokenType.LINE)){
      List<Expr> exprs = new ArrayList<Expr>();
      
      do {
        exprs.add(expression());
        consume(TokenType.LINE);
      } while (!match(TokenType.END));
      
      // A newline is required after end
      consume(TokenType.LINE);
      
      return new BlockExpr(exprs);
    } else {
      return expression();
    }
  }
  
  /**
   * Parses a tuple expression like "a, b, c".
   */
  private Expr tuple() {
    List<Expr> fields = new ArrayList<Expr>();
    
    do {
      fields.add(operator());
    } while (match(TokenType.COMMA));
    
    // Only wrap in a tuple if there are multiple fields.
    if (fields.size() == 1) return fields.get(0);
    
    return new TupleExpr(fields);
  }

  /**
   * Parses a series of operator expressions like "a + b - c".
   */
  private Expr operator() {
    Expr left = call();
    if (left == null) {
      throw new ParseException(":(");
    }
    
    while (match(TokenType.OPERATOR)) {
      String op = last(1).getString();
      Expr right = call();
      if (right == null) {
        throw new ParseException(":(");
      }

      left = new MethodExpr(left, op, right);
    }
    
    return left;
  }

  // The next two functions are a bit squirrely. Function calls like "abs 123"
  // are generally lower precedence than method calls like "123.abs". However,
  // they interact with each other. Some examples will clarify:
  // a b c d  ->  a(b(c(d)))
  // a b c.d  ->  a(b(c.d())
  // a b.c d  ->  a(b.c(d))
  // a b.c.d  ->  a(b.c().d())
  // a.b c d  ->  a.b(c(d))
  // a.b c.d  ->  a.b(c.d())
  // a.b.c d  ->  a.b().c(d)
  // a.b.c.d  ->  a.b().c().d()
  
  /**
   * Parses a series of function calls like "foo bar bang".
   * @return The parsed expression or null if unsuccessful.
   */
  private Expr call() {
    Expr expr = method();
    if (expr == null) return null;
    
    Expr arg = call();
    if (arg == null) return expr;
    
    return new CallExpr(expr, arg);
  }
  
  /**
   * Parses a series of method calls like "foo.bar.bang".
   * @return The parsed expression or null if unsuccessful.
   */
  private Expr method() {
    Expr receiver = primary();
    if (receiver == null) return null;
    
    while (match(TokenType.DOT)) {
      // TODO(bob): Should handle non-name tokens here to support method-like
      // things such as indexers: someArray.234
      String method = consume(TokenType.NAME).getString();

      Expr arg = call();
      if (arg == null) {
        // If the argument is omitted, infer ()
        arg = new NothingExpr();
      }
      receiver = new MethodExpr(receiver, method, arg);
    }
    
    return receiver;
  }
  
  /**
   * Parses a primary expression like a literal.
   * @return The parsed expression or null if unsuccessful.
   */
  private Expr primary() {
    if (match(TokenType.BOOL)){
    return new BoolExpr(last(1).getBool());
    } else if (match(TokenType.INT)) {
      return new IntExpr(last(1).getInt());
    } else if (match(TokenType.STRING)) {
      return new StringExpr(last(1).getString());
    } else if (match(TokenType.NAME)) {
      return new NameExpr(last(1).getString());
    } else if (match(TokenType.LEFT_PAREN, TokenType.RIGHT_PAREN)) {
      return new NothingExpr();
    } else if (match(TokenType.LEFT_PAREN)) {
      Expr expr = expression();
      consume(TokenType.RIGHT_PAREN);
      return expr;
    }
    
    return null;
  }
}
