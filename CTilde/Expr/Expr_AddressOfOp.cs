using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public class Expr_AddressOfOp : Expression
	{
		public Expression ValExpr;

		public Expr_AddressOfOp()
		{
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Symbol.AddressOf);
			ValExpr = Expression.ParseExpression(Tok, Symbol.Semicolon);

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
	}
}
