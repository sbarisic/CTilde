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

		public string OpString {
			get {
				switch (Op) {
					case MathOperation.Add:
						return "+";

					case MathOperation.Sub:
						return "-";

					default:
						throw new NotImplementedException();
				}
			}
		}

		public Expr_MathOp(Expression LExpr) {
			this.LExpr = LExpr;
		}

		public override Expression Parse(Tokenizer Tok) {
			Token T = Tok.NextToken();

			if (T.Is(Symbol.Addition))
				Op = MathOperation.Add;
			else if (T.Is(Symbol.Subtraction))
				Op = MathOperation.Sub;
			else
				throw new NotImplementedException("Unexpected token " + T);

			RExpr = Expression.ParseExpression(Tok, Symbol.Semicolon);
			return this;
		}

		public override string ToSourceStr()
		{
			return string.Format("({0} {1} {2})", LExpr.ToSourceStr(), OpString, RExpr.ToSourceStr());
		}
	}
}
