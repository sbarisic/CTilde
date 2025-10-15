using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{

	public class Expr_IndexOp : Expression
	{
		public Expression LExpr;
		public Expression IndexValExpr;

		public Expr_IndexOp(Expression LExpr)
		{
			this.LExpr = LExpr;
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Symbol.LBracket);
			IndexValExpr = Expression.ParseExpression(Tok, Symbol.RBracket);

			/*Token T = Tok.NextToken();

			if (T.Is(Symbol.Addition))
				Op = MathOperation.Add;
			else if (T.Is(Symbol.Subtraction))
				Op = MathOperation.Sub;
			else
				throw new NotImplementedException("Unexpected token " + T);

			RExpr = Expression.ParseExpression(Tok, Symbol.Semicolon);*/
			return this;
		}

		public override string ToSourceStr()
		{
			return string.Format("{0}[{1}]", LExpr.ToSourceStr(), IndexValExpr.ToSourceStr());
		}
	}
}
