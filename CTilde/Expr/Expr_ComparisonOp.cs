using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public enum ComparisonOp
	{
		Equals,
		NotEquals,
		GreaterThan,
		GreaterThanOrEqual,
		LessThan,
		LessThanOrEqual
	}

	public class Expr_ComparisonOp : Expression
	{
		public Expression LExpr;
		public ComparisonOp Op;
		public Expression RExpr;

		public Expr_ComparisonOp(Expression LExpr)
		{
			this.LExpr = LExpr;
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Token T = Tok.NextToken();

			if (T.Is(Symbol.Equals))
				Op = ComparisonOp.Equals;
			else if (T.Is(Symbol.NotEquals))
				Op = ComparisonOp.NotEquals;
			else if (T.Is(Symbol.GreaterThan))
				Op = ComparisonOp.GreaterThan;
			else if (T.Is(Symbol.GreaterThanOrEqual))
				Op = ComparisonOp.GreaterThanOrEqual;
			else if (T.Is(Symbol.LessThan))
				Op = ComparisonOp.LessThan;
			else if (T.Is(Symbol.LessThanOrEqual))
				Op = ComparisonOp.LessThanOrEqual;
			else
				throw new NotImplementedException("Unexpected token " + T);

			RExpr = Expression.ParseExpression(Tok, Symbol.Semicolon);
			return this;
		}

		public override string ToSourceStr()
		{
			switch (Op)
			{
				case ComparisonOp.Equals:
					return string.Format("{0} == {1}", LExpr.ToSourceStr(), RExpr.ToSourceStr());

				case ComparisonOp.NotEquals:
					return string.Format("{0} != {1}", LExpr.ToSourceStr(), RExpr.ToSourceStr());

				case ComparisonOp.GreaterThan:
					return string.Format("{0} > {1}", LExpr.ToSourceStr(), RExpr.ToSourceStr());

				case ComparisonOp.GreaterThanOrEqual:
					return string.Format("{0} >= {1}", LExpr.ToSourceStr(), RExpr.ToSourceStr());

				case ComparisonOp.LessThan:
					return string.Format("{0} < {1}", LExpr.ToSourceStr(), RExpr.ToSourceStr());

				case ComparisonOp.LessThanOrEqual:
					return string.Format("{0} <= {1}", LExpr.ToSourceStr(), RExpr.ToSourceStr());

				default:
					throw new NotImplementedException();
			}
		}
	}
}
