using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public enum MathOperation {
		Add,
		Sub,
		Mul,
		Div
	}

	public class Expr_MathOp : Expression {
		public Expression LExpr;
		public MathOperation Op;
		public Expression RExpr;

		public override Expression Parse(Tokenizer Tok) {
			LExpr = Expression.ParseExpression(Tok);

			Token T = Tok.NextToken();
			if (T.Is(Symbol.Addition))
				Op = MathOperation.Add;
			else if (T.Is(Symbol.Subtraction))
				Op = MathOperation.Sub;
			else
				throw new NotImplementedException("Throw proper error here");

			RExpr = Expression.ParseExpression(Tok);

			return this;
		}
	}
}
