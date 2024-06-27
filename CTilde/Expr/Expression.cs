using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public abstract class Expression {
		public abstract Expression Parse(Tokenizer Tok);

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

		public static Expression ParseAny(Tokenizer Tok) {
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

				// 
				Expression Var = new Expr_AssignedVariableDef().Parse(Tok);
				Tok.NextToken().Assert(Symbol.Semicolon);
				return Var;

			}

			throw new Exception();
		}

		public static Expression ParseExpression(Tokenizer Tok, Expression LExpr = null) {
			Token[] DebugTokens = GetDebugTokens(Tok);




			if (Tok.Peek().Is(Symbol.Addition)) {


			} else if (Tok.Peek().Is(TokenType.Number) || Tok.Peek().Is(TokenType.Decimal)) {
				return new Expr_ConstNumber().Parse(Tok);
			} else if (Tok.Peek().Is(TokenType.Identifier)) {
				return new Expr_Identifier().Parse(Tok);
			}

			//==


			if (Tok.Peek(2).Is(Symbol.Addition)) {
				Expression Left = ParseExpression(Tok);
			}

			throw new Exception();
		}


		public override string ToString() {
			throw new InvalidOperationException();
		}
	}
}
