using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public abstract class Expression {
		public virtual Expression Parse(Tokenizer Tok) {
			throw new NotImplementedException();
		}

		/*public virtual Expression Parse(Tokenizer Tok, Expression LExpr) {
			throw new NotImplementedException();
		}*/

		public T Parse<T>(Tokenizer Tok) where T : Expression {
			return (T)Parse(Tok);
		}

		static Token[] GetDebugTokens(Tokenizer Tok) {
			Token[] DebugTokens = new Token[5];

			for (int i = 0; i < 5; i++) {
				DebugTokens[i] = Tok.Peek(i + 1);
				Console.WriteLine("{0} - {1}", i, DebugTokens[i]);
			}

			return DebugTokens;
		}

		public static Expression ParseStatement(Tokenizer Tok) {
			Token[] DebugTokens = GetDebugTokens(Tok);

			if (Tok.Peek().Is(Keyword.__ctor) || Tok.Peek().Is(Keyword.__dtor) || (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.LParen))) {

				// Function definition
				return new Expr_FuncDef().Parse(Tok);

			} else if (Tok.Peek().Is(Keyword.@class)) {

				// Class definition
				return new Expr_ClassDef().Parse(Tok);

			} else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.Semicolon)) {

				// Variable definition
				Expression Var = new Expr_VariableDef().Parse(Tok);
				Tok.NextToken().Assert(Symbol.Semicolon);

				return Var;

			} else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.Assignment)) {

				// Variable definition with expression assignment
				Expression Var = new Expr_AssignedVariableDef().Parse(Tok);
				return Var;

			} else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(Symbol.LParen)) {

				// Function call
				Expression Var = new Expr_FuncCall().Parse(Tok);
				return Var;

			}

			throw new Exception();
		}

		public static Expression ParseExpression(Tokenizer Tok) {
			Token[] DebugTokens = GetDebugTokens(Tok);

			Expression LeftExpr = null;

			while (!Tok.Peek().Is(Symbol.Semicolon)) {
				if (LeftExpr != null) {
					if (Tok.Peek().Is(Symbol.Addition) || Tok.Peek().Is(Symbol.Subtraction)) {
						return new Expr_MathOp(LeftExpr).Parse<Expr_MathOp>(Tok);
					}

					throw new InvalidOperationException("Unexpected token " + Tok.Peek());
				}



				if (Tok.Peek().Is(TokenType.Number) || Tok.Peek().Is(TokenType.Decimal)) {
					LeftExpr = new Expr_ConstNumber(Tok.NextToken().Text);
				} else if (Tok.Peek().Is(TokenType.Identifier)) {
					LeftExpr = new Expr_Identifier().Parse<Expr_Identifier>(Tok);
				} else
					throw new NotImplementedException();
			}

			Tok.NextToken().Assert(Symbol.Semicolon);
			return LeftExpr;
		}


		public override string ToString() {
			throw new InvalidOperationException();
		}
	}
}
