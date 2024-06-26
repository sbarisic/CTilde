using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public abstract class Expression {
		//public abstract void Compile(LangProvider Lang);

		public abstract Expression Parse(Tokenizer Tok);

		public T Parse<T>(Tokenizer Tok) where T : Expression {
			return (T)Parse(Tok);
		}

		public override string ToString() {
			throw new NotImplementedException();
		}

		public static Expression ParseAny(Tokenizer Tok) {
			if (Tok.Peek().Is(Keyword.__ctor) || Tok.Peek().Is(Keyword.__dtor) || (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.LParen)))
				return new Expr_FuncDef().Parse(Tok);

			else if (Tok.Peek().Is(Keyword.@class))
				return new Expr_ClassDef().Parse(Tok);

			else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.Semicolon)) {
				Expression Var = new Expr_VariableDef().Parse(Tok);
				Tok.NextToken().Assert(Symbol.Semicolon);
				return Var;
			}
			throw new Exception();
		}
	}
}
