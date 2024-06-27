using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_AssignedVariableDef : Expression {
		public Expr_VariableDef VariableDef;
		public Expression AssignmentValue;

		public override Expression Parse(Tokenizer Tok) {
			VariableDef = new Expr_VariableDef().Parse<Expr_VariableDef>(Tok);

			Tok.NextToken().Assert(Symbol.Assignment);

			AssignmentValue = Expression.ParseExpression(Tok, Symbol.Semicolon);
			return this;
		}
	}
}
