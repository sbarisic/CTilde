using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public class Expr_TypeDef : Expression {
		public string Type;
		public bool IsArray, IsPointer;

		public override Expression Parse(Tokenizer Tok) {
			Type = Tok.NextToken().Assert(TokenType.Identifier).Text;
			if (Tok.Peek().Is(Symbol.Star)) {
				Tok.NextToken();
				IsPointer = true;
			} else if (Tok.Peek().Is(Symbol.LBracket) && Tok.Peek(2).Is(Symbol.RBracket)) {
				Tok.NextToken();
				Tok.NextToken();
				IsArray = true;
			}
			return this;
		}

		public static Expr_TypeDef MakeClassRef(string Name) {
			Expr_TypeDef Ref = new Expr_TypeDef();
			Ref.Type = Name;
			Ref.IsPointer = true;
			return Ref;
		}

		public static Expr_TypeDef MakeVoid() {
			Expr_TypeDef Void = new Expr_TypeDef();
			Void.Type = "void";
			return Void;
		}
	}
}
