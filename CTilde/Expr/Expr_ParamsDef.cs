using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public class Expr_ParamsDef : Expression {
		public List<Expr_VariableDef> Definitions;

		public Expr_ParamsDef() {
			Definitions = new List<Expr_VariableDef>();
		}

		public void Append(Expr_VariableDef Var) {
			Definitions.Add(Var);
		}

		public void Prepend(Expr_VariableDef Var) {
			Definitions.Insert(0, Var);
		}

		public override Expression Parse(Tokenizer Tok) {
			Tok.NextToken().Assert(Symbol.LParen);
			while (!Tok.Peek().IsSymbol(Symbol.RParen)) {
				Append(new Expr_VariableDef().Parse<Expr_VariableDef>(Tok));
				if (!Tok.Peek().IsSymbol(Symbol.RParen))
					Tok.NextToken().Assert(Symbol.Comma);
			}
			Tok.NextToken().Assert(Symbol.RParen);
			return this;
		}
	}
}
