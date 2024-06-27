using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public class Expr_ClassDef : Expression {
		public static Expr_ClassDef CurrentClass;

		public string Name;
		public List<Expr_VariableDef> Variables;
		public List<Expr_FuncDef> Functions;

		public Expr_ClassDef() {
			Functions = new List<Expr_FuncDef>();
			Variables = new List<Expr_VariableDef>();
		}

		public override Expression Parse(Tokenizer Tok) {
			CurrentClass = this;

			Tok.NextToken().Assert(Keyword.@class);
			Name = Tok.NextToken().Assert(TokenType.Identifier).Text;
			Tok.NextToken().Assert(Symbol.LBrace);

			while (!Tok.Peek().Is(Symbol.RBrace)) {
				Expression E = Expression.ParseAny(Tok);

				if (E is Expr_FuncDef) {

					Expr_FuncDef MemberFunc = (Expr_FuncDef)E;
					MemberFunc.FuncParams.Prepend(new ParamDefData(Expr_TypeDef.MakeClassRef(Name), "this"));
					Functions.Add(MemberFunc);

				} else if (E is Expr_VariableDef) {

					Expr_VariableDef Var = (Expr_VariableDef)E;
					Variables.Add(Var);

				} else
					throw new Exception("Unexpected expression type " + E.GetType());
			}

			Tok.NextToken().Assert(Symbol.RBrace);

			CurrentClass = null;
			return this;
		}
	}
}
