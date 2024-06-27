using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_VariableDef : Expression {
		public Expr_TypeDef Type;
		public Expr_Identifier Ident;

		public override Expression Parse(Tokenizer Tok) {
			Type = new Expr_TypeDef().Parse<Expr_TypeDef>(Tok);
			Ident = new Expr_Identifier().Parse<Expr_Identifier>(Tok);
			return this;
		}

		public static Expr_VariableDef MakeThisPtr(string ClassName) {
			Expr_VariableDef ThisVar = new Expr_VariableDef();
			ThisVar.Type = Expr_TypeDef.MakeClassRef(ClassName);
			ThisVar.Ident = new Expr_Identifier("this");
			return ThisVar;
		}
	}
}
