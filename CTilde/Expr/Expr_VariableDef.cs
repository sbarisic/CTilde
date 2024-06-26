using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public class Expr_VariableDef : Expression {
		public Expr_TypeDef Type;
		public string Name;

		public override Expression Parse(Tokenizer Tok) {
			Type = new Expr_TypeDef().Parse<Expr_TypeDef>(Tok);
			Name = Tok.NextToken().Assert(TokenType.Identifier).Text;
			return this;
		}

		public static Expr_VariableDef MakeThisPtr(string ClassName) {
			Expr_VariableDef ThisVar = new Expr_VariableDef();
			ThisVar.Type = Expr_TypeDef.MakeClassRef(ClassName);
			ThisVar.Name = "this";
			return ThisVar;
		}
	}
}
