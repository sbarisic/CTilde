using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr
{
	public class Expr_ReturnStatement : Expression
	{
		public Expression RetValExpr;

		public Expr_ReturnStatement()
		{
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Keyword.@return);

			if (!Tok.Peek().Is(Symbol.Semicolon))
			{
				RetValExpr = Expression.ParseExpression(Tok, Symbol.Semicolon);
			}

			Tok.NextToken().Assert(Symbol.Semicolon);
			return this;
		}
	}
}
