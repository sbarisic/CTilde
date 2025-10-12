using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public class Expr_AssignValue : Expression
	{
		public Expression LExpr;
		public Expression ValueExpr;

		public Expr_AssignValue(Expression LExpr)
		{
			this.LExpr = LExpr;
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Symbol.Assignment);

			ValueExpr = Expression.ParseExpression(Tok, Symbol.Semicolon);
			return this;
		}
	}
}
