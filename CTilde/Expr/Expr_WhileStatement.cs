using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr
{
	public class Expr_WhileStatement : Expression
	{
		public Expression ConditionValue;
		public Expr_Block Body;

		public Expr_WhileStatement()
		{
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Symbol.LParen);
			ConditionValue = Expression.ParseExpression(Tok, Symbol.RParen);

			if (Tok.Peek().Is(Symbol.RParen))
				Tok.NextToken().Assert(Symbol.RParen);

			Tok.Peek().Assert(Symbol.LBrace);
			Body = new Expr_Block().Parse<Expr_Block>(Tok);

			return this;
		}
	}
}
