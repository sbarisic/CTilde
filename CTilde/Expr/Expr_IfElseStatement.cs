using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr
{
	public class Expr_IfElseStatement : Expression
	{
		public Expression ConditionValue;
		public Expr_Block Body;
		public Expression ElseBody;

		public Expr_IfElseStatement()
		{
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Symbol.LParen);
			ConditionValue = Expression.ParseExpression(Tok, Symbol.RParen);
			Tok.NextToken().Assert(Symbol.RParen);

			Tok.Peek().Assert(Symbol.LBrace);
			Body = new Expr_Block().Parse<Expr_Block>(Tok);

			if (Tok.Peek().Is(Keyword.@else))
			{
				Tok.NextToken().Assert(Keyword.@else);

				if (Tok.Peek().Is(Keyword.@if))
				{
					Tok.NextToken().Assert(Keyword.@if);
					ElseBody = new Expr_IfElseStatement().Parse(Tok);
				}
				else
				{
					ElseBody = new Expr_Block().Parse<Expr_Block>(Tok);
				}
			}

			return this;
		}
	}
}
